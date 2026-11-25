// mvpn-helper: la parte del client marshmelloVPN per NAS che non si fa bene in shell.
//
// Crea le chiavi WireGuard del NAS (la privata non lascia mai il NAS), legge il file .rdpmconfig, si registra su
// rdpm-sync con lo stesso flusso del client Windows (account del dispositivo «pc-<nome>», POST /v1/register,
// POST /v1/login, POST /v1/vpn/devices) e scrive il profilo assegnato dal server in un file che lo script
// marshmellovpn.sh legge. Solo libreria standard di Go (più le radici dei certificati di golang.org/x/crypto,
// usate solo se il sistema non ne ha).
//
// Contratto del server: docs/sync-server-api.md. Riferimento del flusso: src/RdpManagerVpn/ClientConfigApplier.cs e
// src/RdpManager.Core/ConfigFileImporter.cs.
package main

import (
	"bytes"
	"crypto/ecdh"
	"crypto/hkdf"
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"

	_ "golang.org/x/crypto/x509roots/fallback" // certificati di riserva se DSM non li espone dove Go li cerca
)

// Versione dell'helper (la stampa «mvpn-helper versione»).
const Version = "1.0.0"

const (
	pbkdf2Iterations = 600_000
	defaultNetwork   = "10.66.0.0/24"
	defaultKeepalive = 25
	privateKeyFile   = "privata.key"
	publicKeyFile    = "pubblica.key"
	accountFile      = "account.json"
	profileFile      = "profilo.env"
)

// ---------- chiavi e segreti (stessi calcoli di server/rdpm-sync/ClientKeys.cs) ----------

func normalizeUser(u string) string { return strings.ToLower(strings.TrimSpace(u)) }

// AuthSecret è il «segreto di accesso» che si manda al server al posto della password.
func AuthSecret(username, password string, iterations int) (string, error) {
	salt := sha256.Sum256([]byte("rdpm-sync/v1/salt/" + normalizeUser(username)))
	master, err := pbkdf2.Key(sha256.New, password, salt[:], iterations, 32)
	if err != nil {
		return "", err
	}
	sub, err := hkdf.Key(sha256.New, master, nil, "rdpm-sync/v1/auth", 32)
	if err != nil {
		return "", err
	}
	return base64.StdEncoding.EncodeToString(sub), nil
}

// NewPrivateKey crea una chiave privata WireGuard (X25519, già «clampata» come fa wg genkey).
func NewPrivateKey() ([]byte, error) {
	k := make([]byte, 32)
	if _, err := rand.Read(k); err != nil {
		return nil, err
	}
	k[0] &= 248
	k[31] &= 127
	k[31] |= 64
	return k, nil
}

// PublicKey ricava la chiave pubblica dalla privata (come wg pubkey).
func PublicKey(private []byte) ([]byte, error) {
	p, err := ecdh.X25519().NewPrivateKey(private)
	if err != nil {
		return nil, err
	}
	return p.PublicKey().Bytes(), nil
}

var keyRe = regexp.MustCompile(`^[A-Za-z0-9+/]{42}[AEIMQUYcgkosw480]=$`)

// ValidKey: 32 byte in base64, 44 caratteri.
func ValidKey(k string) bool {
	if !keyRe.MatchString(k) {
		return false
	}
	b, err := base64.StdEncoding.DecodeString(k)
	return err == nil && len(b) == 32
}

// ---------- nomi ----------

// SafeName come VpnProvisioning.SafeName: lettere, cifre, spazio e . _ - (il resto diventa «-»), al massimo 64.
func SafeName(name string) string {
	var b strings.Builder
	for _, c := range strings.TrimSpace(name) {
		if (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' || c == ' ' {
			b.WriteRune(c)
		} else {
			b.WriteRune('-')
		}
	}
	s := b.String()
	if s == "" {
		return "PC"
	}
	if len(s) > 64 {
		s = s[:64]
	}
	return s
}

// DeviceAccount come ClientConfigApplier.WithDeviceAccount: «pc-» + nome minuscolo senza spazi (al massimo 64, il limite
// del server per i nomi utente).
func DeviceAccount(pcName string) string {
	s := "pc-" + strings.ToLower(strings.ReplaceAll(SafeName(pcName), " ", "-"))
	if len(s) > 64 {
		s = s[:64]
	}
	return s
}

// ---------- indirizzi ----------

// NormalizeURL come SyncHttp.NormalizeUrl: senza schema https:// (http:// per IP e nomi locali), http:// sulle porte 443/8443
// diventa https://, niente barre finali né «/api» o «/v1» in fondo. Il risultato finisce con «/».
func NormalizeURL(raw string) (string, error) {
	text := strings.TrimSpace(raw)
	if text == "" {
		return "", errors.New("indirizzo del server vuoto")
	}
	if !strings.Contains(text, "://") {
		if isLocalHostText(text) {
			text = "http://" + text
		} else {
			text = "https://" + text
		}
	}
	u, err := url.Parse(text)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Hostname() == "" {
		return "", fmt.Errorf("indirizzo del server non valido: %q (esempio: https://vpn.example.com:8443)", raw)
	}
	scheme := u.Scheme
	port := u.Port()
	if scheme == "http" && (port == "443" || port == "8443") {
		scheme = "https"
	}
	path := strings.TrimRight(u.Path, "/")
	for _, suffix := range []string{"/api", "/v1"} {
		if strings.HasSuffix(strings.ToLower(path), suffix) {
			path = strings.TrimRight(path[:len(path)-len(suffix)], "/")
		}
	}
	host := u.Hostname()
	if strings.Contains(host, ":") {
		host = "[" + host + "]"
	}
	if port != "" && !((scheme == "https" && port == "443") || (scheme == "http" && port == "80")) {
		host += ":" + port
	}
	return scheme + "://" + host + path + "/", nil
}

func isLocalHostText(text string) bool {
	host := strings.SplitN(text, "/", 2)[0]
	if strings.HasPrefix(host, "[") {
		return true
	}
	host = strings.SplitN(host, ":", 2)[0]
	return net.ParseIP(host) != nil || !strings.Contains(host, ".") || strings.HasSuffix(strings.ToLower(host), ".local")
}

var hostRe = regexp.MustCompile(`^[A-Za-z0-9.-]{1,253}$`)

// ValidEndpoint: nome-o-IPv4:porta (niente spazi né caratteri strani: il valore finisce in un file letto dalla shell).
func ValidEndpoint(e string) bool {
	host, port, err := net.SplitHostPort(e)
	if err != nil || !hostRe.MatchString(host) {
		return false
	}
	p, err := strconv.Atoi(port)
	return err == nil && p > 0 && p < 65536
}

// ValidCIDR: IPv4 con prefisso.
func ValidCIDR(c string) bool {
	ip, _, err := net.ParseCIDR(c)
	return err == nil && ip.To4() != nil && !strings.ContainsAny(c, " \t")
}

func validCIDRList(s string) bool {
	parts := strings.Split(s, ",")
	for _, p := range parts {
		if !ValidCIDR(strings.TrimSpace(p)) {
			return false
		}
	}
	return len(parts) > 0
}

// ---------- file .rdpmconfig ----------

// Settings: ciò che serve per registrarsi (dal file .rdpmconfig e/o dai parametri).
type Settings struct {
	ServerURL        string
	ServerURLInside  string
	RegistrationCode string
	Username         string
	Password         string
	Endpoint         string
	ServerPublicKey  string
	Network          string
	Keepalive        int
}

type rdpmConfigJSON struct {
	Formato  string          `json:"formato"`
	Versione json.RawMessage `json:"versione"`
	Server   *struct {
		URL                 string `json:"url"`
		URLInterno          string `json:"urlInterno"`
		CodiceRegistrazione string `json:"codiceRegistrazione"`
	} `json:"server"`
	Account *struct {
		Utente   string `json:"utente"`
		Password string `json:"password"`
	} `json:"account"`
	Vpn *struct {
		Endpoint             string `json:"endpoint"`
		ChiavePubblicaServer string `json:"chiavePubblicaServer"`
		Rete                 string `json:"rete"`
		Keepalive            *int   `json:"keepalive"`
	} `json:"vpn"`
}

// stripJSONComments toglie i commenti // e /* */ fuori dalle stringhe e le virgole prima di } o ] (come fa il client
// Windows con CommentHandling.Skip e AllowTrailingCommas).
func stripJSONComments(in []byte) []byte {
	var out bytes.Buffer
	inStr, esc := false, false
	for i := 0; i < len(in); i++ {
		c := in[i]
		if inStr {
			out.WriteByte(c)
			if esc {
				esc = false
			} else if c == '\\' {
				esc = true
			} else if c == '"' {
				inStr = false
			}
			continue
		}
		switch {
		case c == '"':
			inStr = true
			out.WriteByte(c)
		case c == '/' && i+1 < len(in) && in[i+1] == '/':
			for i < len(in) && in[i] != '\n' {
				i++
			}
			out.WriteByte('\n')
		case c == '/' && i+1 < len(in) && in[i+1] == '*':
			i += 2
			for i+1 < len(in) && !(in[i] == '*' && in[i+1] == '/') {
				i++
			}
			i++
		case c == ',':
			j := i + 1
			for j < len(in) && (in[j] == ' ' || in[j] == '\t' || in[j] == '\r' || in[j] == '\n') {
				j++
			}
			if j < len(in) && (in[j] == '}' || in[j] == ']') {
				continue
			}
			out.WriteByte(c)
		default:
			out.WriteByte(c)
		}
	}
	return out.Bytes()
}

// ParseConfig legge il testo di un file .rdpmconfig (formato in src/RdpManager.Core/RdpmConfigFile.cs).
func ParseConfig(text []byte) (Settings, error) {
	text = bytes.TrimPrefix(text, []byte("\xef\xbb\xbf"))
	text = bytes.TrimSpace(text)
	if len(text) == 0 {
		return Settings{}, errors.New("il file di configurazione è vuoto")
	}
	if text[0] != '{' {
		return Settings{}, errors.New("il file non è un .rdpmconfig (JSON): un .conf WireGuard non serve qui, il NAS crea da sé le sue chiavi")
	}
	var c rdpmConfigJSON
	if err := json.Unmarshal(stripJSONComments(text), &c); err != nil {
		return Settings{}, fmt.Errorf("il file non è un JSON valido (%v): chiedi di nuovo il file a chi gestisce il server", err)
	}
	if c.Formato != "" && !strings.EqualFold(c.Formato, "rdpmconfig") {
		return Settings{}, fmt.Errorf("il campo «formato» vale «%s» ma deve essere «rdpmconfig»", c.Formato)
	}
	if len(c.Versione) > 0 {
		v, err := strconv.Atoi(string(c.Versione))
		if err != nil || v < 1 {
			return Settings{}, errors.New("il campo «versione» deve essere un numero intero (1)")
		}
		if v > 1 {
			return Settings{}, fmt.Errorf("il file è della versione %d, più nuova di quella che questo programma conosce (1): aggiorna il pacchetto per NAS", v)
		}
	}
	s := Settings{Keepalive: defaultKeepalive}
	if c.Server != nil {
		s.ServerURL = strings.TrimSpace(c.Server.URL)
		s.ServerURLInside = strings.TrimSpace(c.Server.URLInterno)
		s.RegistrationCode = strings.TrimSpace(c.Server.CodiceRegistrazione)
	}
	if c.Account != nil {
		s.Username = strings.TrimSpace(c.Account.Utente)
		s.Password = c.Account.Password
	}
	if c.Vpn != nil {
		s.Endpoint = strings.TrimSpace(c.Vpn.Endpoint)
		s.ServerPublicKey = strings.TrimSpace(c.Vpn.ChiavePubblicaServer)
		s.Network = strings.TrimSpace(c.Vpn.Rete)
		if c.Vpn.Keepalive != nil {
			s.Keepalive = *c.Vpn.Keepalive
		}
	}
	return s, nil
}

// Validate controlla i valori finali (dopo i parametri, che vincono sul file). Restituisce tutti i problemi.
func (s *Settings) Validate() []string {
	var errs []string
	if s.ServerURL == "" && s.ServerURLInside == "" {
		errs = append(errs, "manca l'indirizzo del server («server.url» nel file, oppure --server)")
	}
	for _, p := range []*string{&s.ServerURL, &s.ServerURLInside} {
		if *p == "" {
			continue
		}
		n, err := NormalizeURL(*p)
		if err != nil {
			errs = append(errs, err.Error())
			continue
		}
		*p = n
	}
	if (s.Username == "") != (s.Password == "") {
		errs = append(errs, "servono sia utente sia password dell'account (o nessuno dei due)")
	}
	if s.Username == "" && s.RegistrationCode == "" {
		errs = append(errs, "manca il codice di registrazione («server.codiceRegistrazione» nel file, oppure --codice)")
	}
	if s.Endpoint != "" && !ValidEndpoint(s.Endpoint) {
		errs = append(errs, fmt.Sprintf("endpoint %q non valido: serve nome-o-IP:porta, per esempio vpn.example.com:51820", s.Endpoint))
	}
	if s.ServerPublicKey != "" && !ValidKey(s.ServerPublicKey) {
		errs = append(errs, "la chiave pubblica del server non è una chiave WireGuard valida (44 caratteri che finiscono con «=»)")
	}
	if s.Network == "" {
		s.Network = defaultNetwork
	} else if !ValidCIDR(s.Network) {
		errs = append(errs, fmt.Sprintf("rete %q non valida: serve IPv4 con prefisso, per esempio 10.66.0.0/24", s.Network))
	}
	if s.Keepalive < 0 || s.Keepalive > 65535 {
		errs = append(errs, "keepalive deve essere tra 0 e 65535 secondi (di solito 25)")
	}
	return errs
}

// Candidates: prima l'indirizzo pubblico, poi quello interno.
func (s *Settings) Candidates() []string {
	var out []string
	if s.ServerURL != "" {
		out = append(out, s.ServerURL)
	}
	if s.ServerURLInside != "" && s.ServerURLInside != s.ServerURL {
		out = append(out, s.ServerURLInside)
	}
	return out
}

// ---------- stato sul NAS ----------

// Account salvato sul NAS (permessi 600): serve per rifare la registrazione con lo stesso account e per «rimuovi».
type Account struct {
	Server   string `json:"server"`
	Servers  []string `json:"servers,omitempty"`
	Username string `json:"utente"`
	Password string `json:"password"`
	Name     string `json:"nome"`
	Device   bool   `json:"accountDelDispositivo"`
}

func writePrivate(path string, data []byte) error {
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		return err
	}
	if err := os.Chmod(tmp, 0o600); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// EnsureKeys legge la chiave privata del NAS o la crea (0600). Restituisce privata e pubblica in base64.
func EnsureKeys(dir string) (string, string, error) {
	path := filepath.Join(dir, privateKeyFile)
	var priv []byte
	if b, err := os.ReadFile(path); err == nil {
		k := strings.TrimSpace(string(b))
		if !ValidKey(k) {
			return "", "", fmt.Errorf("%s non contiene una chiave valida: cancellalo per crearne una nuova (il NAS cambierà chiave)", path)
		}
		priv, _ = base64.StdEncoding.DecodeString(k)
		_ = os.Chmod(path, 0o600)
	} else if errors.Is(err, os.ErrNotExist) {
		if priv, err = NewPrivateKey(); err != nil {
			return "", "", err
		}
		if err := writePrivate(path, []byte(base64.StdEncoding.EncodeToString(priv)+"\n")); err != nil {
			return "", "", err
		}
	} else {
		return "", "", err
	}
	pub, err := PublicKey(priv)
	if err != nil {
		return "", "", err
	}
	pubText := base64.StdEncoding.EncodeToString(pub)
	if err := os.WriteFile(filepath.Join(dir, publicKeyFile), []byte(pubText+"\n"), 0o644); err != nil {
		return "", "", err
	}
	return base64.StdEncoding.EncodeToString(priv), pubText, nil
}

func loadAccount(dir string) (*Account, error) {
	b, err := os.ReadFile(filepath.Join(dir, accountFile))
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var a Account
	if err := json.Unmarshal(b, &a); err != nil {
		return nil, fmt.Errorf("%s non è leggibile: %v", accountFile, err)
	}
	return &a, nil
}

func saveAccount(dir string, a *Account) error {
	b, _ := json.MarshalIndent(a, "", "  ")
	return writePrivate(filepath.Join(dir, accountFile), append(b, '\n'))
}

// ---------- server ----------

// ServerError: il server ha risposto con un errore del contratto.
type ServerError struct {
	Status  int
	Code    string
	Message string
}

func (e *ServerError) Error() string {
	if e.Message != "" {
		return fmt.Sprintf("%s (%d %s)", e.Message, e.Status, e.Code)
	}
	return fmt.Sprintf("errore del server %d %s", e.Status, e.Code)
}

// UnreachableError: il server non risponde (o risponde il proxy con 502/504).
type UnreachableError struct{ Err error }

func (e *UnreachableError) Error() string { return "il server non risponde: " + e.Err.Error() }

// Client parla con rdpm-sync.
type Client struct {
	Base string
	HTTP *http.Client
}

func (c *Client) do(method, path, token string, body any) ([]byte, error) {
	var rd io.Reader
	if body != nil {
		b, _ := json.Marshal(body)
		rd = bytes.NewReader(b)
	}
	req, err := http.NewRequest(method, c.Base+path, rd)
	if err != nil {
		return nil, err
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if token != "" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	req.Header.Set("User-Agent", "marshmelloVPN-NAS/"+Version)
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return nil, &UnreachableError{err}
	}
	defer resp.Body.Close()
	data, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode == http.StatusBadGateway || resp.StatusCode == http.StatusGatewayTimeout {
		return nil, &UnreachableError{fmt.Errorf("il servizio dietro il proxy è spento o si sta riavviando (errore %d)", resp.StatusCode)}
	}
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		se := &ServerError{Status: resp.StatusCode, Code: "error"}
		var e struct {
			Error   string `json:"error"`
			Message string `json:"message"`
		}
		if json.Unmarshal(data, &e) == nil && e.Error != "" {
			se.Code, se.Message = e.Error, e.Message
		} else if resp.StatusCode == http.StatusBadRequest && strings.Contains(string(data), "HTTP request to an HTTPS server") {
			se.Code, se.Message = "bad_request", "l'indirizzo usa «http://» ma il server vuole «https://»"
		}
		return nil, se
	}
	return data, nil
}

// Grant: la risposta di POST /v1/vpn/devices.
type Grant struct {
	Name                string `json:"name"`
	Address             string `json:"address"`
	ServerPublicKey     string `json:"serverPublicKey"`
	Endpoint            string `json:"endpoint"`
	LanEndpoint         string `json:"lanEndpoint"`
	AllowedIPs          string `json:"allowedIPs"`
	DNS                 string `json:"dns"`
	PersistentKeepalive *int   `json:"persistentKeepalive"`
}

// Register crea l'account (se esiste già va bene), accede e registra il NAS. Come ConfigFileImporter.SignInAsync +
// VpnProvisioning.EnsureAsync.
func (c *Client) Register(user, password, code, pcName, publicKey string, iterations int) (*Grant, error) {
	secret, err := AuthSecret(user, password, iterations)
	if err != nil {
		return nil, err
	}
	var codePtr *string
	if code != "" {
		codePtr = &code
	}
	_, regErr := c.do("POST", "v1/register", "", map[string]any{"username": user, "password": secret, "registrationCode": codePtr})
	var se *ServerError
	if regErr != nil && !errors.As(regErr, &se) {
		return nil, regErr // server irraggiungibile
	}
	if regErr != nil && se.Code == "too_many_attempts" {
		return nil, regErr
	}
	// account_exists / registration_disabled: l'account c'è già (o lo crea solo l'amministratore). Anche con un codice
	// mancante o non più valido si prova ad accedere: un NAS già registrato non ha più bisogno del codice.
	token, err := c.login(user, secret, pcName)
	if err != nil {
		if regErr != nil && se.Code != "account_exists" && se.Code != "registration_disabled" {
			return nil, regErr // il motivo vero è la registrazione rifiutata
		}
		return nil, err
	}
	data, err := c.do("POST", "v1/vpn/devices", token, map[string]string{"name": pcName, "publicKey": publicKey})
	if err != nil {
		return nil, err
	}
	var g Grant
	if err := json.Unmarshal(data, &g); err != nil {
		return nil, fmt.Errorf("risposta VPN del server non leggibile: %v", err)
	}
	return &g, nil
}

func (c *Client) login(user, secret, device string) (string, error) {
	data, err := c.do("POST", "v1/login", "", map[string]string{"username": user, "password": secret, "deviceName": device})
	if err != nil {
		return "", err
	}
	var r struct {
		Token string `json:"token"`
	}
	if json.Unmarshal(data, &r) != nil || r.Token == "" {
		return "", errors.New("risposta di accesso del server non valida")
	}
	return r.Token, nil
}

// Remove toglie il NAS dall'account e dal tunnel del server (404 = già tolto).
func (c *Client) Remove(user, password, pcName string, iterations int) error {
	secret, err := AuthSecret(user, password, iterations)
	if err != nil {
		return err
	}
	token, err := c.login(user, secret, pcName)
	if err != nil {
		return err
	}
	_, err = c.do("DELETE", "v1/vpn/devices/"+url.PathEscape(pcName), token, nil)
	var se *ServerError
	if errors.As(err, &se) && se.Status == http.StatusNotFound {
		return nil
	}
	return err
}

// Explain: messaggio chiaro per l'utente (come ConfigFileImporter.Explain).
func Explain(err error) string {
	var se *ServerError
	var ue *UnreachableError
	switch {
	case errors.As(err, &ue):
		return "il server non risponde (" + ue.Err.Error() + "): controlla la connessione a Internet e l'indirizzo del server"
	case errors.As(err, &se) && se.Code == "invalid_credentials":
		return "utente o password sbagliati: se l'account del NAS esiste già con un'altra password (reinstallazione senza la cartella «stato»), scegli un altro nome con --nome oppure fai eliminare l'account sul server (elimina-account)"
	case errors.As(err, &se) && se.Code == "invalid_registration_code":
		return "il codice di registrazione non è accettato dal server: chiedi un file .rdpmconfig nuovo"
	case errors.As(err, &se) && se.Code == "vpn_disabled":
		return "il server non gestisce la VPN automatica"
	case errors.As(err, &se) && se.Code == "too_many_attempts":
		return "troppi tentativi: riprova fra 15 minuti"
	default:
		return err.Error()
	}
}

// ---------- profilo per la shell ----------

// ProfileEnv: il testo di profilo.env (variabili per marshmellovpn.sh). Ogni valore è già validato: niente virgolette,
// spazi o caratteri speciali, quindi il file si può leggere con «.» senza rischi.
func ProfileEnv(g *Grant, name, network string, keepalive int) (string, error) {
	if !ValidKey(g.ServerPublicKey) {
		return "", errors.New("il server non ha mandato una chiave pubblica valida")
	}
	if !ValidCIDR(g.Address) {
		return "", fmt.Errorf("il server ha mandato un indirizzo non valido: %q", g.Address)
	}
	if !ValidEndpoint(g.Endpoint) {
		return "", fmt.Errorf("il server ha mandato un endpoint non valido: %q (sul server manca RDPM_VPN_ENDPOINT?)", g.Endpoint)
	}
	lan := g.LanEndpoint
	if lan != "" && !ValidEndpoint(lan) {
		lan = ""
	}
	allowed := strings.ReplaceAll(g.AllowedIPs, " ", "")
	if allowed == "" || !validCIDRList(allowed) {
		allowed = network
	}
	if g.PersistentKeepalive != nil && *g.PersistentKeepalive >= 0 && *g.PersistentKeepalive <= 65535 {
		keepalive = *g.PersistentKeepalive
	}
	if g.Name != "" {
		name = SafeName(g.Name)
	}
	safeName := strings.ReplaceAll(name, " ", "_") // il nome serve solo per i messaggi
	var b strings.Builder
	fmt.Fprintf(&b, "# Profilo marshmelloVPN di questo NAS, scritto da mvpn-helper %s il %s. Non contiene la chiave privata.\n",
		Version, time.Now().UTC().Format(time.RFC3339))
	fmt.Fprintf(&b, "MVPN_NOME=%s\n", safeName)
	fmt.Fprintf(&b, "MVPN_INDIRIZZO=%s\n", g.Address)
	fmt.Fprintf(&b, "MVPN_CHIAVE_SERVER=%s\n", g.ServerPublicKey)
	fmt.Fprintf(&b, "MVPN_ENDPOINT=%s\n", g.Endpoint)
	fmt.Fprintf(&b, "MVPN_ENDPOINT_LAN=%s\n", lan)
	fmt.Fprintf(&b, "MVPN_ALLOWED_IPS=%s\n", allowed)
	fmt.Fprintf(&b, "MVPN_RETE=%s\n", network)
	fmt.Fprintf(&b, "MVPN_KEEPALIVE=%d\n", keepalive)
	return b.String(), nil
}

// ---------- comandi ----------

type registerOptions struct {
	stateDir   string
	configPath string
	settings   Settings // valori dai parametri (vincono sul file)
	name       string
	iterations int
	timeout    time.Duration
}

func runRegister(o registerOptions, out io.Writer) error {
	s := Settings{Keepalive: defaultKeepalive}
	if o.configPath != "" {
		text, err := os.ReadFile(o.configPath)
		if err != nil {
			return fmt.Errorf("non riesco a leggere il file di configurazione: %v", err)
		}
		if s, err = ParseConfig(text); err != nil {
			return err
		}
	}
	p := o.settings
	for _, kv := range []struct{ dst *string; v string }{
		{&s.ServerURL, p.ServerURL}, {&s.ServerURLInside, p.ServerURLInside}, {&s.RegistrationCode, p.RegistrationCode},
		{&s.Username, p.Username}, {&s.Password, p.Password}, {&s.Endpoint, p.Endpoint},
		{&s.ServerPublicKey, p.ServerPublicKey}, {&s.Network, p.Network},
	} {
		if kv.v != "" {
			*kv.dst = kv.v
		}
	}
	if p.Keepalive > 0 {
		s.Keepalive = p.Keepalive
	}

	if err := os.MkdirAll(o.stateDir, 0o700); err != nil {
		return err
	}
	_ = os.Chmod(o.stateDir, 0o700)
	acc, err := loadAccount(o.stateDir)
	if err != nil {
		return err
	}
	// Reinstallazione o nuova registrazione senza file: si riusa l'account salvato.
	if acc != nil && s.ServerURL == "" && s.ServerURLInside == "" {
		if len(acc.Servers) > 0 {
			s.ServerURL = acc.Servers[0]
			if len(acc.Servers) > 1 {
				s.ServerURLInside = acc.Servers[1]
			}
		} else {
			s.ServerURL = acc.Server
		}
	}
	if acc != nil && s.Username == "" {
		s.Username, s.Password = acc.Username, acc.Password
	}
	name := o.name
	if name == "" && acc != nil {
		name = acc.Name
	}
	if name == "" {
		name, _ = os.Hostname()
	}
	name = SafeName(name)

	device := false
	if s.Username == "" && s.RegistrationCode != "" {
		// Come il client Windows: il NAS si crea un account suo, con una password casuale che resta qui (600).
		pw := make([]byte, 24)
		if _, err := rand.Read(pw); err != nil {
			return err
		}
		s.Username, s.Password, device = DeviceAccount(name), hex.EncodeToString(pw), true
	} else if acc != nil && s.Username == acc.Username {
		device = acc.Device
	}
	if errs := s.Validate(); len(errs) > 0 {
		return errors.New(strings.Join(errs, "; "))
	}

	_, pub, err := EnsureKeys(o.stateDir)
	if err != nil {
		return fmt.Errorf("chiavi: %v", err)
	}
	// L'account si salva prima della chiamata: se la rete cade, al tentativo dopo si riusa (stessa chiave = stesso indirizzo).
	acc = &Account{Server: s.Candidates()[0], Servers: s.Candidates(), Username: s.Username, Password: s.Password, Name: name, Device: device}
	if err := saveAccount(o.stateDir, acc); err != nil {
		return err
	}
	fmt.Fprintf(out, "Nome nella VPN: %s; account: %s; chiave pubblica: %s\n", name, s.Username, pub)

	var grant *Grant
	var last error
	for _, base := range s.Candidates() {
		c := &Client{Base: base, HTTP: &http.Client{Timeout: o.timeout}}
		fmt.Fprintf(out, "Registrazione su %s ...\n", strings.TrimSuffix(base, "/"))
		grant, last = c.Register(s.Username, s.Password, s.RegistrationCode, name, pub, o.iterations)
		if last == nil {
			acc.Server = base
			_ = saveAccount(o.stateDir, acc)
			break
		}
		var ue *UnreachableError
		if !errors.As(last, &ue) {
			break // il server ha risposto: l'altro indirizzo non cambierebbe niente
		}
	}
	if grant == nil {
		return errors.New("registrazione non riuscita: " + Explain(last))
	}
	if s.ServerPublicKey != "" && grant.ServerPublicKey != s.ServerPublicKey {
		fmt.Fprintln(out, "Attenzione: la chiave pubblica del server nel file è diversa da quella che il server ha mandato; uso quella del server (il file è vecchio?).")
	}
	env, err := ProfileEnv(grant, name, s.Network, s.Keepalive)
	if err != nil {
		return err
	}
	if err := writePrivate(filepath.Join(o.stateDir, profileFile), []byte(env)); err != nil {
		return err
	}
	lan := grant.LanEndpoint
	if lan == "" {
		lan = "nessuno"
	}
	fmt.Fprintf(out, "Registrato: indirizzo VPN %s, endpoint %s, endpoint LAN %s.\n", grant.Address, grant.Endpoint, lan)
	return nil
}

func runRemove(stateDir string, iterations int, timeout time.Duration, out io.Writer) error {
	acc, err := loadAccount(stateDir)
	if err != nil {
		return err
	}
	if acc == nil {
		return errors.New("questo NAS non risulta registrato (manca " + accountFile + ")")
	}
	servers := acc.Servers
	if len(servers) == 0 {
		servers = []string{acc.Server}
	}
	var last error
	for _, base := range servers {
		c := &Client{Base: base, HTTP: &http.Client{Timeout: timeout}}
		if last = c.Remove(acc.Username, acc.Password, acc.Name, iterations); last == nil {
			fmt.Fprintf(out, "NAS «%s» tolto dalla VPN sul server.\n", acc.Name)
			return nil
		}
		var ue *UnreachableError
		if !errors.As(last, &ue) {
			break
		}
	}
	return errors.New("non sono riuscito a togliere il NAS dal server: " + Explain(last))
}

func usage(w io.Writer) {
	fmt.Fprint(w, `mvpn-helper `+Version+` (marshmelloVPN per NAS)

  mvpn-helper registra --stato CARTELLA [--config FILE.rdpmconfig] [--nome NOME] [--server URL] [--server-interno URL]
                       [--codice CODICE] [--utente U --password P] [--endpoint HOST:PORTA] [--chiave-server CHIAVE] [--rete CIDR]
  mvpn-helper rimuovi  --stato CARTELLA
  mvpn-helper pubblica --stato CARTELLA      (stampa la chiave pubblica, la crea se manca)
  mvpn-helper versione
`)
}

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(args []string, out, errOut io.Writer) int {
	if len(args) == 0 {
		usage(errOut)
		return 2
	}
	cmd := args[0]
	fs := flag.NewFlagSet(cmd, flag.ContinueOnError)
	fs.SetOutput(errOut)
	state := fs.String("stato", "", "cartella dello stato (chiavi, account, profilo)")
	var o registerOptions
	fs.StringVar(&o.configPath, "config", "", "file .rdpmconfig")
	fs.StringVar(&o.name, "nome", "", "nome del NAS nella VPN (predefinito: hostname)")
	fs.StringVar(&o.settings.ServerURL, "server", "", "indirizzo del server rdpm-sync")
	fs.StringVar(&o.settings.ServerURLInside, "server-interno", "", "indirizzo del server dentro la VPN")
	fs.StringVar(&o.settings.RegistrationCode, "codice", "", "codice di registrazione")
	fs.StringVar(&o.settings.Username, "utente", "", "account esistente (facoltativo)")
	fs.StringVar(&o.settings.Password, "password", "", "password dell'account (facoltativa)")
	fs.StringVar(&o.settings.Endpoint, "endpoint", "", "endpoint pubblico del server WireGuard")
	fs.StringVar(&o.settings.ServerPublicKey, "chiave-server", "", "chiave pubblica del server WireGuard")
	fs.StringVar(&o.settings.Network, "rete", "", "rete della VPN (predefinita 10.66.0.0/24)")
	iterations := fs.Int("iterazioni", pbkdf2Iterations, "solo per le prove")
	timeout := fs.Duration("timeout", 20*time.Second, "tempo massimo per ogni chiamata al server")

	switch cmd {
	case "versione", "--version", "-v":
		fmt.Fprintln(out, Version)
		return 0
	case "aiuto", "help", "-h", "--help":
		usage(out)
		return 0
	case "registra", "rimuovi", "pubblica":
	default:
		fmt.Fprintf(errOut, "comando sconosciuto: %s\n", cmd)
		usage(errOut)
		return 2
	}
	if err := fs.Parse(args[1:]); err != nil {
		return 2
	}
	if *state == "" {
		fmt.Fprintln(errOut, "manca --stato")
		return 2
	}
	var err error
	switch cmd {
	case "registra":
		o.stateDir, o.iterations, o.timeout = *state, *iterations, *timeout
		err = runRegister(o, out)
	case "rimuovi":
		err = runRemove(*state, *iterations, *timeout, out)
	case "pubblica":
		if err = os.MkdirAll(*state, 0o700); err == nil {
			var pub string
			if _, pub, err = EnsureKeys(*state); err == nil {
				fmt.Fprintln(out, pub)
			}
		}
	}
	if err != nil {
		fmt.Fprintln(errOut, "Errore: "+err.Error())
		return 1
	}
	return 0
}
