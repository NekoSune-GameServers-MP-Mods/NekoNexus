# Automatic bot map training

The realtime game server learns a separate navigation graph for each map ID while human players
are in matches. It does not need a manual training session:

- Client-supplied spawn lists become team-tagged spawn anchors.
- Plausible human movement creates directed route edges and stores the observed grounded, jump,
  water, and movement-key states. Teleports and implausibly fast position changes are ignored.
- Human deaths raise the danger score around that location and on routes leading into it.
- Bots explore low-traffic routes with fewer recorded deaths, and can replay observed movement
  states when following a learned path.

The graph is saved as `map-<id>.json`. By default it is under `data/bot-maps` beside the realtime
server binaries. Set `NEKONEXUS_BOT_MAP_DATA` to choose another directory. The Docker Compose game
server uses `/data/bot-maps`, mounted to `./server-data/bot-maps`, so training survives container
recreation. The server flushes map memory at shutdown and checkpoints it during play.

Training is observational. The dedicated server has no Unity map colliders, so it cannot prove a
route is physically clear or identify a jump pad from its asset/collider. Jump launch locations are
inferred from observed jumping or strong upward movement. The AI continues to require recent
client-reported hit evidence for retaliatory shots, which is the available map-physics sight check;
it does not fire merely because a player is nearby.

To reset a map's learned routes, stop the realtime game server and remove that map's JSON file from
the configured training directory. Training files should be backed up like other server data.
