# QoE_Shell Performance Notes

The merged `QoE_Shell` scene loads all four environments (Training, City,
Hotel, Museum) at once — ~70M triangles — which struggles even tethered/PC-
rendered. The study is teleport-based: the subject only ever occupies **one**
agent area at a time. So the whole strategy is *render only the scene the
subject is currently in*.

Items are ordered by impact-per-effort. Anything marked **editor step** must be
done by you in Unity (Claude only edits code).

---

## 1. Scene-root culling (biggest win, code already done) — **editor step to enable**

`QoeDeviceClient` now has a `Scene Culling` section with a `sceneRoots`
`GameObject[4]` field. When assigned, each teleport activates only the target
scene's root and disables the other three (~75% fewer triangles in the frame).
It's a no-op until you assign it, so nothing changed yet.

**To enable:** select the `QoeDeviceClient` GameObject and drag the four scene
roots into `Scene Roots` in this order:

| index | root object (scene hierarchy) |
|---|---|
| 0 | `Training` |
| 1 | `CityScene` |
| 2 | `HotelScene` |
| 3 | `MuseumScene` |

Mapping is baked in code: task 0 → Training, 1–3 → City, 4–6 → Hotel,
7–9 → Museum. At scene start all roots stay active (so every `ActivationZone`
registers); culling kicks in on the first teleport. Verify the agent in each
scene still talks after teleporting in — the active scene's `ActivationZone` is
what selects the prompt/voice, and it's a child of the root being toggled.

> If you'd rather not disable a whole root (e.g. you keep one shared skybox or
> light under a scene root), move those shared objects out from under the four
> roots first, or tell me and I'll switch culling to a per-renderer approach.

## 2. Drop the Quality level from Ultra → Medium/Low — **editor step**

`ProjectSettings → Quality` currently defaults to **Ultra** (level 5). That's
the wrong preset for Quest 3. Set the default (and the Android row) to **Medium**
or **Low**. The project already ships `Low/Medium/_PipelineAsset` URP assets, so
this is a one-click change. Expect a large GPU-time drop with little visible
difference at headset resolution.

## 3. URP render settings (whichever pipeline asset the active level uses) — **editor step**

On the active `*_PipelineAsset` (Low has HDR on and render scale 1):
- **Render Scale:** start at `1.0`; if still GPU-bound, `0.85`–`0.9` is a cheap win.
- **HDR:** turn **off** for the Android/Quest asset unless you need it (Low has
  `m_SupportsHDR: 1`). HDR doubles framebuffer bandwidth.
- **MSAA:** 2× is usually the sweet spot on Quest; 4× only if you have headroom.
- **Main/Additional light shadows:** the Low asset already disables shadows
  (`m_MainLightShadowsSupported: 0`). Keep them off, or cap `Shadow Distance`
  very low (5–10 m) — the subject stands still in front of one agent.

## 4. Static batching + GPU instancing — **editor step**

- Mark non-moving environment meshes (walls, floors, props) as **Static** (or at
  least *Batching Static*) so Unity can static-batch them.
- Ensure shared materials have **GPU Instancing** ticked where meshes repeat.
- This mainly cuts draw calls / CPU, complementing the triangle cut from §1.

## 5. Occlusion culling — **editor step (optional)**

After §1 each scene is isolated, but within a scene (especially the Museum/City)
there's still a lot off-camera. Bake **Occlusion Culling** (`Window → Rendering →
Occlusion Culling → Bake`) so rooms behind walls aren't drawn. Lower payoff than
§1–§2 but free at runtime once baked.

## 6. Mesh-level reduction (highest effort, do last)

70M tris means some source assets are absurdly dense for VR. If §1–§5 aren't
enough:
- Run the imported environment meshes through a decimation/LOD step (Unity LOD
  Groups, or simplify in DCC). Museum sculptures/artifacts are the usual
  offenders.
- Add LODGroups so distant detail drops automatically.

---

## Quick sanity check after enabling §1

Use the QoE debug task buttons (Training, Task 1–9) to teleport between scenes
and watch the **Stats** panel (Game view → Stats) — "Tris" should drop to roughly
one scene's worth after the first teleport. The `[Qoe] perf:` log line confirms
which root is active.
