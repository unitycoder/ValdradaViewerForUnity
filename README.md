<img src="https://img.shields.io/badge/VibeCoded-100%25-green" alt="AI Generated Content"/>

# Valdrada Viewer for Unity

<img width="1276" height="541" alt="image" src="https://github.com/user-attachments/assets/856c2a7d-eab9-4ce1-8327-9b4f7cfcce30" />

Unity Package Manager (UPM) importer for [Valdrada](https://github.com/mkturkcan/valdrada) v0.6.0 `CTL1` city tiles. An early prototype to view real-world New York City geometry directly in the Unity Editor.

**Version:** 0.2.0 | **Unity:** 6000.0+ | **Package:** `com.unitycoder.valdradaviewer`

## Current support

| Feature | Status |
| --- | --- |
| Valdrada `t_X_Z.bin` tile decoding | Yes |
| Building footprints, heights and approximate colours | Yes |
| Basic extruded building shells and simple flat roofs | Yes |
| Asphalt, sidewalks, curbs and lane markings | Yes |
| Terrain heightfields | Yes |
| Multiple adjacent tiles and world positioning | Yes |
| Built-in RP / URP / HDRP Lit materials | Basic support |
| Mesh colliders | Optional |
| Trees, props, vehicles, procedural facades, detailed roofs | Not yet |
| Runtime streaming, LOD and batch optimization | Not yet |

## Install

In Unity: **Window > Package Manager > + > Install package from git URL**, then enter:

```text
https://github.com/unitycoder/ValdradaViewerForUnity.git
```

For a local checkout, use **Install package from disk** and select the repository's `package.json`.

If upgrading from the old `Assets/Valdrada/` importer: **remove the old `Assets/Valdrada/Editor/ValdradaTileImporter.cs`** first to prevent two windows sharing the same menu command. Existing generated `.asset` files may still reference the previous `UnityLibrary.Valdrada.ValdradaTileAsset` script. Leave that old `Assets/Valdrada/ValdradaTileAsset.cs` in place until you have reimported or removed those assets. The package uses a new namespace and its own runtime asset class.

## Get the city data

The **dataset is not included** in this Git repository or Unity package. Download the Valdrada v0.6.0 tiles separately from [Hugging Face](https://huggingface.co/datasets/mehmetkeremturkcan/valdrada):

```powershell
hf download mehmetkeremturkcan/valdrada --repo-type dataset --revision v0.6.0 --include "Content/tiles/*" --local-dir "D:\Valdrada"
```

See [upstream licensing](https://github.com/mkturkcan/valdrada/blob/main/LICENSING.md) before distributing imported content.

## Import a tile

1. In Unity, select **Tools > Valdrada > Tile Importer**.
2. Browse to `Content/tiles/t_X_Z.bin` (not `far_X_Z.bin`). The tile may remain outside your Unity project.
3. Keep **Convert Three.js Coordinates (Flip Z)** enabled for readable road markings.
4. For the first import, set **Max Buildings** to `100` and enable buildings, surfaces and terrain.
5. Select **Import Tile**, then save the scene.
6. Increase **Max Buildings** to `0` for the full tile, or import neighboring tiles.

The importer creates a tile GameObject in the active scene. Generated meshes and materials are saved persistently in `Assets/ValdradaGenerated/` (not inside the package). Importing again creates new assets; clean up unneeded generated assets and GameObjects manually.

### Tile positioning

Tiles are 512 x 512 metres. The first imported tile becomes the local scene origin; later tiles are offset by their X/Z tile coordinates. Use **Reset Scene Origin** before importing a new, distant area and remove old scene tile roots. Keep the same Flip Z setting across neighboring tiles.

## Known limitations

- Early prototype tested with Valdrada v0.6.0 binary format only. No dataset files are bundled.
- Courtyard buildings get inner walls but no roof mesh; complex outer polygons can lack roofs if ear-clipping fails.
- Flat-color materials only: this package does not yet connect the downloaded PBR textures or detailed glTF models.
- No runtime tile loader; tiles are imported and baked as meshes in the Editor. Many tiles can create a large number of GameObjects.
- The first import for a new area sets an EditorPrefs-based tile origin for that Unity project. Reset it before switching areas.

## Repository structure

```text
ValdradaViewerForUnity/
  package.json
  Runtime/
    UnityCoder.ValdradaViewer.Runtime.asmdef
    ValdradaTileAsset.cs
  Editor/
    UnityCoder.ValdradaViewer.Editor.asmdef
    ValdradaTileImporter.cs
  README.md
  CHANGELOG.md
  LICENSE.md
  THIRD_PARTY_NOTICES.md
```

## Credits and licensing

This is an unofficial Unity importer and is not affiliated with Valdrada. Original Valdrada project by [Mehmet Kerem Turkcan](https://github.com/mkturkcan/valdrada). The package source is released under the MIT license; **the Valdrada data and media have separate licenses**, including ODbL and CC BY. Details in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
