using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace SamplePlugin.Services;

public class ActService : IDisposable
{
    private readonly IPluginLog log;
    private ClientWebSocket? webSocket;
    private CancellationTokenSource? cancellationTokenSource;
    private Task? receiveTask;

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
        var buffer = new byte[4096];

        try
        {
            log.Information("[ActService] Starting receive loop...");
            while (!token.IsCancellationRequested && webSocket?.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    log.Information($"[ActService] Received message ({result.Count} bytes): {json.Substring(0, Math.Min(200, json.Length))}...");
                    ParseAndUpdateDps(json);
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

            // Get player character name from somewhere - for now we'll look for it
            // Try to find "YOU" first, then try to find the player name
            JToken? playerData = combatant["YOU"];
            
            if (playerData == null)
            {
                log.Warning("[ActService] No 'YOU' entry in Combatant data");
                return;
            }

            log.Information($"[ActService] Player data fields: {string.Join(", ", ((JObject)playerData).Properties().Select(p => $"{p.Name}:{p.Value}"))}");

            var dpsString = playerData["DPS"]?.ToString();
            log.Information($"[ActService] Raw DPS string: '{dpsString}'");
            
            if (string.IsNullOrEmpty(dpsString))
            {
                log.Warning("[ActService] No DPS data found");
                return;
            }

            if (double.TryParse(dpsString, out var dps))
            {
                // Ignore infinite or NaN values
                if (double.IsInfinity(dps) || double.IsNaN(dps))
                {
                    log.Debug($"[ActService] Ignoring invalid DPS value: {dps}");
                    return;
                }

                log.Information($"[ActService] Successfully parsed DPS: {dps:F1}");
                
                // Extract job info
                var jobString = playerData["Job"]?.ToString();
                log.Information($"[ActService] Job data: {jobString}");
                
                OnDpsDataReceived?.Invoke(this, new DpsDataEventArgs { PersonalDps = dps, JobId = jobString });
            }
            else
            {
                log.Warning($"[ActService] Failed to parse DPS value: {dpsString}");
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
