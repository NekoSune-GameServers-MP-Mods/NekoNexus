# Custom weapons and cosmetics

Shop data can add an item or change its stats, name, and price when its `PrefabName` points to a prefab already available in the client. This reuses the existing item's visuals and behavior.

New visuals require Unity assets and a client-side asset loader. New weapon behavior, such as a different projectile or sound, also requires client code. The finished assets must be packaged for the Unity version used by the game and registered by the client mod before the item is created.

Shop entries are defined in `packages/ws/src/seed/shop.json`. The seed command only adds missing items to populated databases; it does not synchronize removals or edits. Existing shop changes therefore need an explicit database migration or maintenance step. Retired items should be taken off sale while preserving inventory and loadouts unless owners specifically request those to be removed too.
