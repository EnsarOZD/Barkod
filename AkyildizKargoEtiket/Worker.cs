using Microsoft.Extensions.Options;

namespace AkyildizKargoEtiket;

public class PrintAgentWorker : BackgroundService
{
    private readonly PrintAgentService _svc;
    private readonly AgentOptions      _opts;
    private readonly ILogger<PrintAgentWorker> _log;

    public PrintAgentWorker(PrintAgentService svc, IOptions<AgentOptions> opts, ILogger<PrintAgentWorker> log)
    {
        _svc  = svc;
        _opts = opts.Value;
        _log  = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("AkyildizKargoEtiket servisi başladı. Sunucu: {Url}", _opts.ServerUrl);

        // İlk kayıt
        await RegisterSafeAsync(ct);

        var pollInterval   = TimeSpan.FromSeconds(_opts.PollIntervalSeconds);
        var registerEvery  = TimeSpan.FromSeconds(30); // her 30 sn heartbeat
        var lastRegistered = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Periyodik heartbeat/re-register (yazıcı listesi güncellenmiş olabilir)
                if (DateTime.UtcNow - lastRegistered > registerEvery)
                {
                    await RegisterSafeAsync(ct);
                    lastRegistered = DateTime.UtcNow;
                }

                var jobs = await _svc.GetPendingJobsAsync(ct);

                foreach (var job in jobs)
                {
                    if (ct.IsCancellationRequested) break;

                    // Printing (1)
                    await _svc.UpdateJobStatusAsync(job.Id, 1, null, ct);

                    try
                    {
                        _svc.PrintJob(job);
                        // Done (2)
                        await _svc.UpdateJobStatusAsync(job.Id, 2, null, ct);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Baskı hatası — Job#{Id}", job.Id);
                        // Failed (3)
                        await _svc.UpdateJobStatusAsync(job.Id, 3, ex.Message, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Polling hatası, {Interval}sn sonra tekrar deneniyor.", _opts.PollIntervalSeconds);
            }

            await Task.Delay(pollInterval, ct);
        }

        _log.LogInformation("AkyildizKargoEtiket servisi durdu.");
    }

    private async Task RegisterSafeAsync(CancellationToken ct)
    {
        try { await _svc.RegisterAsync(ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Kayıt başarısız, sonraki döngüde tekrar denenecek."); }
    }
}
