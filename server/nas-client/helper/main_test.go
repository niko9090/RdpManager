package main

import (
	"bytes"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

func TestAuthSecretVettoreDelContratto(t *testing.T) {
	// docs/sync-server-api.md, «Vettori di prova».
	got, err := AuthSecret("  Mario ", "correct horse battery staple", pbkdf2Iterations)
	if err != nil {
		t.Fatal(err)
	}
	if want := "LYH1gkWICwAF8nBgnDp+hpjco7z+BEnEW50myOk2GcA="; got != want {
		t.Fatalf("segreto %s, atteso %s", got, want)
	}
}

func TestPublicKeyRfc7748(t *testing.T) {
	priv, _ := hex.DecodeString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")
	pub, err := PublicKey(priv)
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(pub); got != "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a" {
		t.Fatalf("pubblica %s", got)
	}
}

func TestNewPrivateKeyClampata(t *testing.T) {
	k, err := NewPrivateKey()
	if err != nil {
		t.Fatal(err)
	}
	if k[0]&7 != 0 || k[31]&128 != 0 || k[31]&64 == 0 {
		t.Fatalf("chiave non clampata: %x", k)
	}
	if !ValidKey(base64.StdEncoding.EncodeToString(k)) {
		t.Fatal("chiave non valida")
	}
}

func TestSafeNameEAccountDelDispositivo(t *testing.T) {
	cases := map[string]string{"DS920+": "DS920-", "  nas casa ": "nas casa", "": "PC", "àb": "-b"}
	for in, want := range cases {
		if got := SafeName(in); got != want {
			t.Errorf("SafeName(%q) = %q, atteso %q", in, got, want)
		}
	}
	if got := DeviceAccount("NAS Casa"); got != "pc-nas-casa" {
		t.Errorf("account %q", got)
	}
	if got := DeviceAccount(strings.Repeat("a", 80)); len(got) != 64 {
		t.Errorf("account lungo %d", len(got))
	}
}

func TestNormalizeURL(t *testing.T) {
	cases := map[string]string{
		"vpn.example.com:8443":         "https://vpn.example.com:8443/",
		"http://vpn.example.com:8443/": "https://vpn.example.com:8443/",
		"https://vpn.example.com/v1":   "https://vpn.example.com/",
		"10.66.0.1:8787":               "http://10.66.0.1:8787/",
		"http://10.66.0.1:8787/api/":   "http://10.66.0.1:8787/",
		"https://x.example.com:443":    "https://x.example.com/",
	}
	for in, want := range cases {
		got, err := NormalizeURL(in)
		if err != nil || got != want {
			t.Errorf("NormalizeURL(%q) = %q, %v; atteso %q", in, got, err, want)
		}
	}
	for _, bad := range []string{"", "ftp://x", "https://"} {
		if _, err := NormalizeURL(bad); err == nil {
			t.Errorf("NormalizeURL(%q) doveva fallire", bad)
		}
	}
}

func TestValidEndpointRifiutaCaratteriPericolosi(t *testing.T) {
	for _, ok := range []string{"vpn.example.com:51820", "192.168.1.10:51820"} {
		if !ValidEndpoint(ok) {
			t.Errorf("%q doveva essere valido", ok)
		}
	}
	for _, bad := range []string{"vpn.example.com", "a b:1", "x;rm -rf /:1", "$(id):51820", "h:0", "h:70000", "'h':1"} {
		if ValidEndpoint(bad) {
			t.Errorf("%q doveva essere rifiutato", bad)
		}
	}
}

const fileEsempio = `{
  "_commento": "File di configurazione di RdpManager",
  "formato": "rdpmconfig",
  "versione": 1,
  // commento in stile JavaScript, accettato come nel client Windows
  "server": { "url": "https://vpn.example.com:8443", "urlInterno": "http://10.66.0.1:8787", "codiceRegistrazione": "codice-segreto" },
  "vpn": { "endpoint": "vpn.example.com:51820", "chiavePubblicaServer": "hSDwCYkwp1R0i33ctD73Wg2/Og0mOBr066SpjqqbTmo=", "rete": "10.66.0.0/24", "keepalive": 25, "richiediChiaveAlServer": true, },
}`

func TestParseConfig(t *testing.T) {
	s, err := ParseConfig([]byte("\xef\xbb\xbf" + fileEsempio))
	if err != nil {
		t.Fatal(err)
	}
	if s.ServerURL != "https://vpn.example.com:8443" || s.ServerURLInside != "http://10.66.0.1:8787" || s.RegistrationCode != "codice-segreto" ||
		s.Endpoint != "vpn.example.com:51820" || s.Network != "10.66.0.0/24" || s.Keepalive != 25 {
		t.Fatalf("letto male: %+v", s)
	}
	if errs := s.Validate(); len(errs) > 0 {
		t.Fatalf("problemi: %v", errs)
	}
	if c := s.Candidates(); len(c) != 2 || c[0] != "https://vpn.example.com:8443/" || c[1] != "http://10.66.0.1:8787/" {
		t.Fatalf("candidati %v", c)
	}
}

func TestParseConfigErrori(t *testing.T) {
	for _, bad := range []string{"", "[Interface]\nPrivateKey = x", `{"formato":"altro"}`, `{"versione":2}`, `{ rotto`} {
		if _, err := ParseConfig([]byte(bad)); err == nil {
			t.Errorf("%q doveva fallire", bad)
		}
	}
	s := Settings{Endpoint: "senza-porta", ServerPublicKey: "corta", Network: "10.66.0.0"}
	if errs := s.Validate(); len(errs) < 4 {
		t.Fatalf("attesi almeno 4 problemi, avuti %v", errs)
	}
}

// finto rdpm-sync: registra, login, dispositivi.
type fakeServer struct {
	mu       sync.Mutex
	accounts map[string]string
	devices  map[string]string // nome -> chiave
	code     string
	calls    []string
	lan      string
}

func (f *fakeServer) handler() http.Handler {
	mux := http.NewServeMux()
	errJSON := func(w http.ResponseWriter, status int, code string) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		_ = json.NewEncoder(w).Encode(map[string]any{"error": code, "message": "x"})
	}
	mux.HandleFunc("POST /v1/register", func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		defer f.mu.Unlock()
		var b struct{ Username, Password string; RegistrationCode *string }
		_ = json.NewDecoder(r.Body).Decode(&b)
		f.calls = append(f.calls, "register "+b.Username)
		if b.RegistrationCode == nil || *b.RegistrationCode != f.code {
			errJSON(w, 403, "invalid_registration_code")
			return
		}
		if _, ok := f.accounts[b.Username]; ok {
			errJSON(w, 409, "account_exists")
			return
		}
		f.accounts[b.Username] = b.Password
		w.WriteHeader(201)
	})
	mux.HandleFunc("POST /v1/login", func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		defer f.mu.Unlock()
		var b struct{ Username, Password, DeviceName string }
		_ = json.NewDecoder(r.Body).Decode(&b)
		f.calls = append(f.calls, "login "+b.Username)
		if p, ok := f.accounts[b.Username]; !ok || p != b.Password {
			errJSON(w, 401, "invalid_credentials")
			return
		}
		_ = json.NewEncoder(w).Encode(map[string]string{"token": "tok-" + b.Username})
	})
	mux.HandleFunc("POST /v1/vpn/devices", func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		defer f.mu.Unlock()
		if !strings.HasPrefix(r.Header.Get("Authorization"), "Bearer tok-") {
			errJSON(w, 401, "unauthorized")
			return
		}
		var b struct{ Name, PublicKey string }
		_ = json.NewDecoder(r.Body).Decode(&b)
		f.calls = append(f.calls, "device "+b.Name)
		f.devices[b.Name] = b.PublicKey
		_ = json.NewEncoder(w).Encode(map[string]any{
			"name": b.Name, "address": "10.66.0.7/32", "serverPublicKey": "hSDwCYkwp1R0i33ctD73Wg2/Og0mOBr066SpjqqbTmo=",
			"endpoint": "vpn.example.com:51820", "lanEndpoint": f.lan, "allowedIPs": "10.66.0.0/24", "dns": "", "persistentKeepalive": 25,
		})
	})
	mux.HandleFunc("DELETE /v1/vpn/devices/{name}", func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		defer f.mu.Unlock()
		name := r.PathValue("name")
		f.calls = append(f.calls, "delete "+name)
		if _, ok := f.devices[name]; !ok {
			errJSON(w, 404, "not_found")
			return
		}
		delete(f.devices, name)
		w.WriteHeader(204)
	})
	return mux
}

func newFake() *fakeServer {
	return &fakeServer{accounts: map[string]string{}, devices: map[string]string{}, code: "codice-segreto", lan: "192.168.1.10:51820"}
}

func TestRegistraRimuoviConServerFinto(t *testing.T) {
	f := newFake()
	srv := httptest.NewServer(f.handler())
	defer srv.Close()
	dir := t.TempDir()
	cfg := filepath.Join(dir, "nas.rdpmconfig")
	text := strings.Replace(fileEsempio, "https://vpn.example.com:8443", srv.URL, 1)
	text = strings.Replace(text, "http://10.66.0.1:8787", "http://127.0.0.1:1", 1)
	if err := os.WriteFile(cfg, []byte(text), 0o600); err != nil {
		t.Fatal(err)
	}
	state := filepath.Join(dir, "stato")
	var out, errOut bytes.Buffer
	if rc := run([]string{"registra", "--stato", state, "--config", cfg, "--nome", "NAS Casa", "--iterazioni", "10"}, &out, &errOut); rc != 0 {
		t.Fatalf("rc=%d %s %s", rc, out.String(), errOut.String())
	}
	env, err := os.ReadFile(filepath.Join(state, profileFile))
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"MVPN_INDIRIZZO=10.66.0.7/32\n", "MVPN_ENDPOINT=vpn.example.com:51820\n", "MVPN_ENDPOINT_LAN=192.168.1.10:51820\n",
		"MVPN_CHIAVE_SERVER=hSDwCYkwp1R0i33ctD73Wg2/Og0mOBr066SpjqqbTmo=\n", "MVPN_RETE=10.66.0.0/24\n", "MVPN_KEEPALIVE=25\n", "MVPN_NOME=NAS_Casa\n"} {
		if !strings.Contains(string(env), want) {
			t.Errorf("profilo senza %q:\n%s", want, env)
		}
	}
	if strings.Contains(string(env), "PrivateKey") {
		t.Error("il profilo non deve contenere la chiave privata")
	}
	for _, name := range []string{privateKeyFile, accountFile, profileFile} {
		st, err := os.Stat(filepath.Join(state, name))
		if err != nil {
			t.Fatal(err)
		}
		if mode := st.Mode().Perm(); mode != 0o600 && os.PathSeparator == '/' {
			t.Errorf("%s ha permessi %o", name, mode)
		}
	}
	priv, _ := os.ReadFile(filepath.Join(state, privateKeyFile))
	pubSent := f.devices["NAS Casa"]
	raw, _ := base64.StdEncoding.DecodeString(strings.TrimSpace(string(priv)))
	pub, _ := PublicKey(raw)
	if base64.StdEncoding.EncodeToString(pub) != pubSent {
		t.Fatal("al server deve arrivare la pubblica corrispondente alla privata del NAS")
	}
	if _, ok := f.accounts["pc-nas-casa"]; !ok {
		t.Fatalf("account del dispositivo non creato: %v", f.calls)
	}

	// Seconda registrazione (reinstallazione con la stessa cartella): stesso account, stessa chiave, nessun errore.
	out.Reset()
	if rc := run([]string{"registra", "--stato", state, "--config", cfg, "--iterazioni", "10"}, &out, &errOut); rc != 0 {
		t.Fatalf("seconda registrazione rc=%d %s", rc, errOut.String())
	}
	if f.devices["NAS Casa"] != pubSent {
		t.Fatal("la chiave non deve cambiare alla seconda registrazione")
	}
	// Senza file: riusa l'account salvato.
	if rc := run([]string{"registra", "--stato", state, "--iterazioni", "10"}, &out, &errOut); rc != 0 {
		t.Fatalf("registrazione senza file rc=%d %s", rc, errOut.String())
	}

	if rc := run([]string{"rimuovi", "--stato", state, "--iterazioni", "10"}, &out, &errOut); rc != 0 {
		t.Fatalf("rimuovi rc=%d %s", rc, errOut.String())
	}
	if _, ok := f.devices["NAS Casa"]; ok {
		t.Fatal("il NAS doveva sparire dal server")
	}
	// Già tolto: 404 va bene.
	if rc := run([]string{"rimuovi", "--stato", state, "--iterazioni", "10"}, &out, &errOut); rc != 0 {
		t.Fatalf("rimuovi due volte rc=%d %s", rc, errOut.String())
	}
}

func TestRegistraProvaIlSecondoIndirizzoSeIlPrimoNonRisponde(t *testing.T) {
	f := newFake()
	srv := httptest.NewServer(f.handler())
	defer srv.Close()
	state := t.TempDir()
	var out, errOut bytes.Buffer
	rc := run([]string{"registra", "--stato", state, "--server", "http://127.0.0.1:1", "--server-interno", srv.URL,
		"--codice", "codice-segreto", "--nome", "ds", "--iterazioni", "10", "--timeout", "2s"}, &out, &errOut)
	if rc != 0 {
		t.Fatalf("rc=%d %s", rc, errOut.String())
	}
}

func TestRegistraCodiceSbagliato(t *testing.T) {
	f := newFake()
	srv := httptest.NewServer(f.handler())
	defer srv.Close()
	var out, errOut bytes.Buffer
	rc := run([]string{"registra", "--stato", t.TempDir(), "--server", srv.URL, "--codice", "sbagliato", "--iterazioni", "10"}, &out, &errOut)
	if rc != 1 || !strings.Contains(errOut.String(), "codice di registrazione non è accettato") {
		t.Fatalf("rc=%d %s", rc, errOut.String())
	}
}

func TestProfileEnvRifiutaValoriPericolosi(t *testing.T) {
	ka := 25
	g := &Grant{Address: "10.66.0.7/32", ServerPublicKey: "hSDwCYkwp1R0i33ctD73Wg2/Og0mOBr066SpjqqbTmo=", Endpoint: "$(reboot):1", PersistentKeepalive: &ka}
	if _, err := ProfileEnv(g, "n", defaultNetwork, 25); err == nil {
		t.Fatal("endpoint pericoloso accettato")
	}
	g.Endpoint, g.LanEndpoint, g.AllowedIPs = "vpn.example.com:51820", "`id`:1", "10.66.0.0/24; reboot"
	env, err := ProfileEnv(g, "n", defaultNetwork, 25)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(env, "MVPN_ENDPOINT_LAN=\n") || !strings.Contains(env, "MVPN_ALLOWED_IPS=10.66.0.0/24\n") {
		t.Fatalf("valori non ripuliti:\n%s", env)
	}
}

func TestVersione(t *testing.T) {
	var out bytes.Buffer
	if rc := run([]string{"versione"}, &out, &out); rc != 0 || strings.TrimSpace(out.String()) != Version {
		t.Fatal(out.String())
	}
}
