#if ANDROID && SAEPAR_PROBE_DIAGNOSTICS
using System.Text.Json;
using Android.Util;
using SaeParTunnel.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using SaeParTunnel.App.ViewModels;

namespace SaeParTunnel.App.Platforms.Android;

// Opt-in Debug-only device test. Invalid local configuration prevents any
// outbound connection. No hooks or simulated hangs are present in Release.
internal static class AndroidProbeDiagnostics
{
    internal static async Task RunAsync()
    {
        var process = new AndroidProbeProcess();
        const string config = "{\"inbounds\":[],\"outbounds\":[{\"protocol\":\"not-a-protocol\"}]}";
        try
        {
            var vm = IPlatformApplication.Current!.Services.GetRequiredService<MainViewModel>();
            await vm.InitializeAsync();
            Log.Info("SaeParProbeCheck", $"archive-restored: profiles={vm.TotalProfiles}; home={vm.HomeServers.Count}; selected={vm.SelectedHealthyProfile is not null}");
            var initial = await process.RunAsync(config, 19381, 1, CancellationToken.None);
            using (var response = JsonDocument.Parse(initial))
                if (response.RootElement.GetProperty("success").GetBoolean()) throw new Exception("Invalid config unexpectedly succeeded.");
            Log.Info("SaeParProbeCheck", "native-local-invalid-config: PASS");
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await process.RunAsync("SAEPAR_TEST_HANG", 19381, 1, cancel.Token);
                throw new Exception("Hung probe unexpectedly completed.");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Cancellation was too slow.");
            Log.Info("SaeParProbeCheck", $"hung-worker-cancel: PASS ({watch.ElapsedMilliseconds} ms)");
            var restarted = await process.RunAsync(config, 19381, 1, CancellationToken.None);
            using (var response = JsonDocument.Parse(restarted))
                if (response.RootElement.GetProperty("success").GetBoolean()) throw new Exception("Invalid config unexpectedly succeeded.");
            Log.Info("SaeParProbeCheck", "worker-restart-main-survived: PASS");
            watch.Restart();
            try
            {
                await process.RunAsync("SAEPAR_TEST_HANG", 19381, 1, CancellationToken.None);
                throw new Exception("Watchdog did not stop the hung probe.");
            }
            catch (TunnelStartupException) { }
            if (watch.Elapsed > TimeSpan.FromSeconds(9)) throw new Exception("Watchdog was too slow.");
            var recovered = await process.RunAsync(config, 19381, 1, CancellationToken.None);
            using (var response = JsonDocument.Parse(recovered))
                if (response.RootElement.GetProperty("success").GetBoolean()) throw new Exception("Invalid config unexpectedly succeeded.");
            Log.Info("SaeParProbeCheck", "watchdog-stop-restart-main-survived: PASS");
        }
        catch (Exception ex) { Log.Error("SaeParProbeCheck", "FAIL: " + ex.GetType().Name + ": " + ex.Message); }
        finally { await process.StopAsync(CancellationToken.None); }
    }
}
#endif
