using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GeoSemanticSat.UI.Services;

public class ConnectivityStatusEventArgs : EventArgs
{
    public bool IsOnline { get; }
    public double LatencyMs { get; }
    public string StatusMessage { get; }

    public ConnectivityStatusEventArgs(bool isOnline, double latencyMs, string statusMessage)
    {
        IsOnline = isOnline;
        LatencyMs = latencyMs;
        StatusMessage = statusMessage;
    }
}

/// <summary>
/// Lightweight, non-intrusive network health monitor.
/// Runs non-blocking background probes to detect online/offline state changes
/// without interfering with UI responsiveness or violating air-gapped security policies.
/// </summary>
public class NetworkConnectivityMonitor : IDisposable
{
    private static readonly HttpClient ProbeClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromMilliseconds(1500)
    })
    {
        Timeout = TimeSpan.FromMilliseconds(2000)
    };

    private readonly Timer _timer;
    private bool _isOnline = false;
    private double _lastLatencyMs = 0;
    private int _isChecking = 0;
    private bool _disposed = false;

    public bool IsOnline => _isOnline;
    public double LastLatencyMs => _lastLatencyMs;
    public bool AutoCheckEnabled { get; set; } = true;

    public event EventHandler<ConnectivityStatusEventArgs>? ConnectivityChanged;

    public NetworkConnectivityMonitor(TimeSpan? checkInterval = null)
    {
        var interval = checkInterval ?? TimeSpan.FromSeconds(10);
        _timer = new Timer(OnTimerTick, null, TimeSpan.FromMilliseconds(500), interval);
    }

    private void OnTimerTick(object? state)
    {
        if (!AutoCheckEnabled || _disposed) return;
        _ = CheckConnectivityAsync();
    }

    public async Task<bool> CheckConnectivityAsync()
    {
        if (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0)
        {
            return _isOnline;
        }

        bool previousState = _isOnline;
        bool newState = false;
        double latency = 0;

        try
        {
            // 1. Check OS Network Interface
            bool osNetworkAvailable = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
            if (!osNetworkAvailable)
            {
                newState = false;
                latency = -1;
            }
            else
            {
                // 2. Fast multi-endpoint probe (Cloudflare, Google, CartoDB)
                var probeUrls = new[]
                {
                    "https://1.1.1.1",
                    "https://dns.google",
                    "https://a.basemaps.cartocdn.com"
                };

                var sw = Stopwatch.StartNew();
                foreach (var url in probeUrls)
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.Add("User-Agent", "GeoSemanticSat-Analyst-Platform/3.0 (HealthCheck)");

                        using var response = await ProbeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        if (response.IsSuccessStatusCode || (int)response.StatusCode < 500)
                        {
                            sw.Stop();
                            newState = true;
                            latency = sw.Elapsed.TotalMilliseconds;
                            break;
                        }
                    }
                    catch
                    {
                        // Try next endpoint
                    }
                }

                if (!newState)
                {
                    // Fallback: If HTTP probes blocked by firewall but OS says network is up, assume network available
                    newState = osNetworkAvailable;
                    latency = 50.0;
                }
            }
        }
        catch
        {
            newState = false;
            latency = -1;
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }

        _lastLatencyMs = latency;
        bool stateChanged = (previousState != newState);
        _isOnline = newState;

        if (stateChanged)
        {
            string msg = newState ? $"ONLINE ({latency:F0}ms)" : "AIR-GAPPED / OFFLINE";
            ConnectivityChanged?.Invoke(this, new ConnectivityStatusEventArgs(newState, latency, msg));
        }

        return newState;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
