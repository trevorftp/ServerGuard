# ServerGuard

ServerGuard conceals fully enclosed ore before chunk data reaches clients and mixes ore decoys into the surrounding host rocks. It also filters occluded non-player entities through native tracking and can send optional creature decoys.

## Configuration

Edit `ModConfig/serverguard.json`, then restart the server.

Optional [Integrated Mod Manager (IMM)](https://mods.vintagestory.at/imm) support lets you edit these settings in-game. Changes still require a server restart. IMM is not required.

### Default settings

- **ConcealOre: true**  
  Enables ore protection.

- **DecoyPercent: 12**  
  Ore decoy density. Accepts 0 to 50%. Set to 0 for concealment only.

- **ChunkCacheMiB: 64**  
  Compressed chunk cache limit. Accepts 1 to 1024 MiB.

- **ConcealEntities: true**  
  Enables entity protection and creature decoys.

- **ConcealPlayers: false**  
  Includes real players in entity concealment. Requires `ConcealEntities`.

- **AlwaysVisibleRange: 16**  
  Entities within this distance remain visible. Accepts 8 to 64 blocks.

- **EntityRayBudgetPerThread: 512**  
  Real-entity visibility raycasts allowed per thread every 200 ms. Accepts 16 to 16384. Decoys have a separate allowance of this value, capped at 512.

- **EntityDecoysPerPlayer: 8**  
  Target creature decoys per player. Accepts 0 to 16. Set to 0 to disable.

## Server command

Use `/serverguard` to check protection settings and counters. Requires the `controlserver` permission.
