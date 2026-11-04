# Firefighting Simulation — Smoke Demo

Branch: `demosmoke`

Burning objects keep the Ignis flame and release Ignis smoke blocks. The smoke drifts through open indoor air and stays under the roof.

## Open the project

1. Open this folder in Unity.
2. Open `Assets/SCENARIOS/Scenario 1 (controlled; 1 story; daytime) 1.unity`.
3. Press **Play**.
4. Watch the **Game** view.

## Smoke

- Flames stay on the Ignis fire.
- Each burning object releases smoke blocks.
- Blocks move through doorways and around walls and furniture.
- A solid `RoofCollider` under each ceiling keeps the smoke inside the room.

## Record a camera tour

Use **Tools → Kitchen Smoke Tour → Record Play View**.

The camera holds for about 10 seconds in the kitchen, then the hallway, then outside the main door.

## Add missing roof colliders

Use **Tools → Kitchen Smoke Tour → Add Roof Colliders**.

Exit Play mode first. The menu adds a solid box under every ceiling and saves the scene.
