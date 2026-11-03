using RdpmSync;

var options = SyncOptions.FromEnvironment();

if (args.Length > 0)
    return Cli.Run(args, options);

if (options.AdminKey.Length is > 0 and < 16)
{
    Console.Error.WriteLine("RDPM_SYNC_ADMIN_KEY è troppo corta: servono almeno 16 caratteri.");
    return 2;
}

var app = SyncApp.Build(options);
app.Logger.LogInformation("rdpm-sync {Version} in ascolto su {Listen}:{Port}; amministrazione via HTTP {Admin}",
    SyncApp.Version, options.ListenAddress, options.Port, options.AdminKey.Length > 0 ? "attiva" : "spenta");
app.Run();
return 0;
