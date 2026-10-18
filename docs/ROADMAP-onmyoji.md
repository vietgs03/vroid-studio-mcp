# Fork roadmap: Onmyoji-style characters (QingXi / Shiranui)

Working notes for the `vietgs03` fork. Goal: let an agent build an Onmyoji-style character in VRoid
Studio 2.14.0 (proportions measured from the Shiranui game rig, look of QingXi), then hand a VRM to
Blender for the parts VRoid cannot make.

## What upstream v1.0.0 already covers

| Area | Coverage | Notes |
|---|---|---|
| Face | 16 tabs, presets + numeric/colour sliders | Face Sets alone has 92 sliders |
| Body | `Whole Body`: 23 sliders + skin colours | height, head size, neck, shoulders, chest, arms, hands, torso, waist, legs, feet |
| Hairstyle | presets per tab + Main/Highlight colour | **no hair shape**: the Design Editor (guides, groups, procedural params) is not exposed |
| Outfit | presets + per-item blend-shape sliders | stock items only |
| Camera / screenshots | full | the visual feedback loop works |
| Save / open / create | full | |

Baseline on WSL2 + Windows: `tools/wsl-dev.sh test dev --model ...` → 146/146.

## Gaps that matter for an Onmyoji look (priority order)

1. **VRM export** (blocks the Blender hand-off). Engine entry points are known
   (`docs/internal-api.md` §15): `EditRootScreen.ViewModel.EnterToVRMExport()`,
   `VRMExportScreen.ViewModel`, `VRoidStudio.Exporter.VRoidExporter`, `ExportSettings`.
   Interop also exposes `ExportVrm`, `ExportToPath`, `ExportWithNoDialogNoSplit`. Plan: a blocking
   `export_vrm <path> [settings]` tool resolved on the exporter's completion signal.
2. **Texture layers** (the anime/Onmyoji look lives in textures: eyes, face paint, clothing
   patterns, hair gradients). Interop has `ImportLayer`, `ImportLayerFromCloset`, `AddTexture`,
   `ExportLayer`. Plan: `texture_import <section> <category> <png>` to add a layer to the current
   item's texture, so textures painted elsewhere (or projected from the game textures) can be pushed in.
3. **Custom items** (reuse finished parts across models). Interop has `ImportCustomItemByTransferableType`,
   `LoadCustomItemContentFromPath`, `ExportSelectedTemplateAsCustomItem`. Plan: import/export
   `.vroidcustomitem` and select custom items (today only the stock `Presets` group is selectable).
4. **Hair Design Editor** (twin tails, buns, Onmyoji fringe). Classes are mapped in
   `internal-api.md` §12 (`HairEditor.DesignEditor.*`: Float/Int/Enum/Curve parameters, materials).
   Largest effort; groups/guides are procedural and need their own command set.
5. **XWear import** (`ImportXWearFromDialog`): outfits made in Blender/Unity → dress-up + auto-fit.
   Needs a no-dialog path.
6. **Accessories section** (not exposed upstream).

## Measurement mapping (Shiranui rig → Body sliders)

`OnmyojiModel3D/qx5/shiranui_measure.json` holds bone positions from the game rig (height ≈ 1.65 m
to the head bone, pelvis 1.12, knee 0.75, ankle 0.27, shoulder span 0.23, arm reach 0.55). The Body
sliders are normalised (-1..1) with no units, so the mapping is done by measurement, not by formula:
export a VRM (gap 1), read bone positions in Blender, fit each slider by bisection against the
Shiranui ratios (leg/height, head/height, shoulder/height, arm/height). This needs gap 1 first.

## Dev loop

```
tools/wsl-dev.sh cycle                  # build, stop dev, deploy dev, start dev, wait ready
tools/wsl-dev.sh test dev --model <C:\...\model.vroid>
tools/wsl-dev.sh log dev
```

`prod` (the real install, jcode MCP server `vroid` on 17891) is only redeployed deliberately with
`tools/wsl-dev.sh deploy prod` after the dev sandbox passes the harness.
