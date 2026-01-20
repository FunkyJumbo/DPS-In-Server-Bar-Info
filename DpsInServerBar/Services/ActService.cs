using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace DpsInServerBar.Services;

public class ActService : IDisposable
{
    private readonly IPluginLog log;
    private ClientWebSocket? webSocket;
    private CancellationTokenSource? cancellationTokenSource;
    private Task? receiveTask;
    private string? lastJobId;

    public event EventHandler<DpsDataEventArgs>? OnDpsDataReceived;

    public ActService(IPluginLog log)
    {
        this.log = log;
    }

    public void Connect(string host = "127.0.0.1", int port = 10501)
    {
        if (webSocket != null && webSocket.State == WebSocketState.Open)
        {
            log.Debug("[ActService] Already connected");
            return;
        }

        try
        {
            log.Information("[ActService] Attempting to connect to OverlayPlugin...");
            webSocket = new ClientWebSocket();
            cancellationTokenSource = new CancellationTokenSource();
            
            var uri = new Uri($"ws://{host}:{port}/ws");
            log.Information($"[ActService] URI: {uri}");
            
            webSocket.ConnectAsync(uri, cancellationTokenSource.Token).Wait();
            
            log.Information("[ActService] Successfully connected to OverlayPlugin WebSocket");
            
            // Subscribe to CombatData events
            SubscribeToCombatData();
            
            receiveTask = ReceiveLoop(cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[ActService] Failed to connect to OverlayPlugin");
            Dispose();
        }
    }

    private void SubscribeToCombatData()
    {
        try
        {
            var subscription = new
            {
                call = "subscribe",
                events = new[] { "CombatData" }
            };
            
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(subscription);
            var bytes = Encoding.UTF8.GetBytes(json);
            
            webSocket?.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationTokenSource?.Token ?? CancellationToken.None).Wait();
            
            log.Information("[ActService] Sent subscription request for CombatData");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[ActService] Failed to subscribe to CombatData");
        }
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

        try
        {
            log.Information("[ActService] Starting receive loop...");
            while (!token.IsCancellationRequested && webSocket?.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var fragment = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    messageBuilder.Append(fragment);

                    // Only process when we have the complete message
                    if (result.EndOfMessage)
                    {
                        var json = messageBuilder.ToString();
                        messageBuilder.Clear();
                        
                        log.Debug($"[ActService] Received complete message ({json.Length} bytes)");
                        ParseAndUpdateDps(json);
                    }
                }
            }
            log.Information($"[ActService] Receive loop ended. WebSocket state: {webSocket?.State}");
        }
        catch (OperationCanceledException)
        {
            log.Information("[ActService] Receive loop cancelled");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[ActService] Error in receive loop");
        }
    }

    private void ParseAndUpdateDps(string json)
    {
        try
        {
            var data = JObject.Parse(json);
            
            // Check if this is CombatData event
            var type = data["type"]?.ToString();
            log.Information($"[ActService] Event type: {type}");
            
            if (type != "CombatData")
            {
                log.Debug($"[ActService] Ignoring non-CombatData event: {type}");
                return;
            }

            var combatant = data["Combatant"];
            if (combatant == null)
            {
                log.Warning("[ActService] No Combatant data in message");
                return;
            }

            log.Information($"[ActService] Combatant keys: {string.Join(", ", ((JObject)combatant).Properties().Select(p => p.Name))}");

            // Find player data - look for "YOU" first, or any key containing "(YOU)" but not "Chocobo"
            JToken? playerData = combatant["YOU"];
            
            if (playerData == null)
            {
                // When in a party/group, the key is like "PlayerName (YOU)"
                // Ignore chocobo entries
                var youKey = ((JObject)combatant).Properties()
                    .FirstOrDefault(p => p.Name.Contains("(YOU)") && !p.Name.Contains("Chocobo", StringComparison.OrdinalIgnoreCase))?.Name;
                
                if (youKey != null)
                {
                    playerData = combatant[youKey];
                    log.Information($"[ActService] Found player data under key: {youKey}");
                }
            }
            
            if (playerData == null)
            {
                log.Warning("[ActService] No 'YOU' entry in Combatant data");
                return;
            }

            log.Information($"[ActService] Player data fields: {string.Join(", ", ((JObject)playerData).Properties().Select(p => $"{p.Name}:{p.Value}"))}");

            // Try EncDPS first, then try encdps, then DPS as fallback
            var encDpsString = playerData["EncDPS"]?.ToString() ?? 
                               playerData["encdps"]?.ToString() ?? 
                               playerData["DPS"]?.ToString();
            log.Information($"[ActService] Raw EncDPS string: '{encDpsString}'");
            
            if (string.IsNullOrEmpty(encDpsString))
            {
                log.Warning($"[ActService] No EncDPS/DPS data found. Available fields: {string.Join(", ", ((JObject)playerData).Properties().Select(p => p.Name))}");
                return;
            }

            if (double.TryParse(encDpsString, out var dps))
            {
                // Ignore infinite or NaN values
                if (double.IsInfinity(dps) || double.IsNaN(dps))
                {
                    log.Debug($"[ActService] Ignoring invalid EncDPS value: {dps}");
                    return;
                }

                log.Information($"[ActService] Successfully parsed EncDPS: {dps:F1}");
                
                // Extract job info
                var jobString = playerData["Job"]?.ToString();
                lastJobId = jobString;
                log.Information($"[ActService] Job data: {jobString}");
                
                OnDpsDataReceived?.Invoke(this, new DpsDataEventArgs { PersonalDps = dps, JobId = jobString });
            }
            else
            {
                log.Warning($"[ActService] Failed to parse EncDPS value: {encDpsString}");
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[ActService] Failed to parse JSON");
        }
    }

    public void Dispose()
    {
        if (receiveTask != null)
        {
            cancellationTokenSource?.Cancel();
            try
            {
                receiveTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        webSocket?.Dispose();
        cancellationTokenSource?.Dispose();
    }
}

public class DpsDataEventArgs : EventArgs
{
    public double PersonalDps { get; set; }
    public string? JobId { get; set; }
}
