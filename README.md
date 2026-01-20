
# DPS In Server Bar

Clean DPS display integrated in your FFXIV server info bar.

This plugin shows your current encounter DPS (EncDPS) directly in the DTR bar (“server info bar”). It reads combat events from ACT via OverlayPlugin’s WebSocket server for accurate and up-to-date values.

Repo : https://raw.githubusercontent.com/FunkyJumbo/DPS-In-Server-Bar-Info/master/pluginmaster.json

## Requirements

- ACT + OverlayPlugin installed and running
- OverlayPlugin WebSocket server (WSServer) enabled
  - Default address: `ws://127.0.0.1:10501/ws`
  - Keep ACT + OverlayPlugin running while playing

If the WebSocket server is disabled or blocked (firewall), the plugin cannot read combat data.



## Usage

- Enter combat: EncDPS updates in the DTR bar with your job.
- Exit combat: plugin keeps listening for 2 seconds, then shows `●` to mark the final value.

## Troubleshooting

- No updates: verify OverlayPlugin WSServer is enabled on `127.0.0.1:10501`.
- Final dot missing: ensure the plugin remained active for 2 seconds after combat.
- Firewall: allow ACT/OverlayPlugin on localhost.

## License

See `LICENSE.md`.
