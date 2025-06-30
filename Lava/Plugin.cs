using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using Unosquare.Labs.EmbedIO;
using Unosquare.Labs.EmbedIO.Modules;
using Unosquare.Labs.EmbedIO.WebApi;

namespace LavaAdminPlugin
{
    [ApiVersion(2, 1)]
    public class LavaAdminPlugin : TerrariaPlugin
    {
        public override string Name => "LavaAdminPanel";
        public override string Author => "aTurtleGod";
        public override string Description => "Web-based plugin GUI manager with OTP authentication.";
        public override Version Version => new Version(1, 0, 0);

        private WebServer server;
        private static string otpCode;
        private static DateTime otpGeneratedAt;
        private static HashSet<string> disabledPlugins = new();

        public LavaAdminPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            ServerApi.Hooks.GameInitialize.Register(this, OnGameInitialize);
            GenerateOtp();
            StartWebServer();
        }

        private void OnGameInitialize(EventArgs args)
        {
            Console.WriteLine("\n🔥 Lava v1.0 - Admin Panel Active 🔥");
            Console.WriteLine($"🔐 One-Time Passcode: {otpCode} (valid for 10 minutes)\n");
        }

        public override void Dispose()
        {
            ServerApi.Hooks.GameInitialize.Deregister(this, OnGameInitialize);
            server?.Dispose();
            base.Dispose();
        }

        private void GenerateOtp()
        {
            otpCode = new Random().Next(100000, 999999).ToString();
            otpGeneratedAt = DateTime.UtcNow;
        }

        private void StartWebServer()
        {
            string webRoot = Path.Combine("ServerPlugins", "LavaAdminPlugin", "Web", "ui");

            server = new WebServer(o => o
                    .WithUrlPrefix("http://*:7878")
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithWebApi("/api", m => m
                    .WithController(() => new PluginController(otpCode, otpGeneratedAt, disabledPlugins)))
                .WithModule(new FileModule("/ui", webRoot, true))
                .WithModule(new ActionModule("/", HttpVerbs.Get, ctx =>
                    ctx.SendStringAsync(File.ReadAllText(Path.Combine(webRoot, "index.html")), "text/html", Encoding.UTF8)));

            server.RunAsync();
        }
    }

    public class PluginController : WebApiController
    {
        private readonly string otp;
        private readonly DateTime issuedAt;
        private readonly HashSet<string> disabledPlugins;

        public PluginController(string otp, DateTime issuedAt, HashSet<string> disabledPlugins)
        {
            this.otp = otp;
            this.issuedAt = issuedAt;
            this.disabledPlugins = disabledPlugins;
        }

        private bool IsAuthorized(HttpContext ctx)
        {
            var token = ctx.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
            return token == otp && (DateTime.UtcNow - issuedAt).TotalMinutes < 10;
        }

        [Route(HttpVerbs.Get, "/plugins/list")]
        public async Task ListPlugins()
        {
            if (!IsAuthorized(HttpContext))
            {
                HttpContext.Response.StatusCode = 401;
                return;
            }

            var plugins = TShockAPI.TShock.Plugins.Select(p => new
            {
                p.Name,
                Version = p.Version.ToString(),
                p.Author,
                Enabled = !disabledPlugins.Contains(p.Name)
            });

            await HttpContext.SendDataAsync(plugins);
        }

        [Route(HttpVerbs.Post, "/plugins/toggle")]
        public async Task TogglePlugin()
        {
            if (!IsAuthorized(HttpContext))
            {
                HttpContext.Response.StatusCode = 401;
                return;
            }

            string pluginName = HttpContext.Request.QueryString["name"];
            if (string.IsNullOrEmpty(pluginName))
            {
                HttpContext.Response.StatusCode = 400;
                await HttpContext.SendStringAsync("Missing plugin name", "text/plain", Encoding.UTF8);
                return;
            }

            if (disabledPlugins.Contains(pluginName))
                disabledPlugins.Remove(pluginName);
            else
                disabledPlugins.Add(pluginName);

            await HttpContext.SendStringAsync("Plugin state toggled.", "text/plain", Encoding.UTF8);
        }

        [Route(HttpVerbs.Post, "/auth/validate")]
        public async Task ValidateOtp()
        {
            var request = await HttpContext.ParseJsonBodyAsync<Dictionary<string, string>>();
            bool success = request.TryGetValue("otp", out var code) &&
                           code == otp &&
                           (DateTime.UtcNow - issuedAt).TotalMinutes < 10;

            await HttpContext.SendDataAsync(new { success });
        }
    }
}
