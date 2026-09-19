# Cover Art (local override)

The catalog (`docs/games.json`) carries remote `coverArtUrl` values, so nothing needs to live here
for a normal build. This folder is only a manual escape hatch.

`GameDetailPanel` checks `Resources/CoverArt/{game-id}.png` *before* the remote URL. Drop a PNG/JPG here
named after the entry's `id` and it wins over the hosted art, no catalog change required.

## Import settings
Texture Type **Sprite (2D and UI)** is required (Unity defaults this for PNGs in 2D projects).
Recommended ~512×288 (16:9), but the image is rendered with `preserveAspect`, so any aspect works.
