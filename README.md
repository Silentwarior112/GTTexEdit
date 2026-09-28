# GTTexEdit

Texture editor suite for models found in Gran Turismo 3 and Gran Turismo 4: Cars, Wheels, Tires, Courses, and any loose ModelSets or Tex1 files are all editable.
While not intended, the tool can also read any file that has a Tex1 embedded in it.

**Other Tex1 tools:**
- [Tex1 plugin for Paint.NET](https://github.com/Silentwarior112/PDN-Plugins)  Directly edit loose Tex1 files
- [GTGPB:](https://github.com/Silentwarior112/GTGPB/releases)  .gpb extractor + Tex1 converter

## What it does

### Texture list & Viewer
<p align="center">
  <img src="https://github.com/Silentwarior112/GTTexEdit/blob/main/docs/variation-editor.png">
</p>

- The tool presents the content of a Tex1 on a per-buffer level, as those are the real objects inside them.
  Most buffers are single textures, but some contain multiple. These multi-texture buffers employ
  complex optimization techniques that save on blocks:
  - Utilizing unused gaps in an image to put additional image(s) in them (Atlasing)
  - Utilizing unused buffer bytes that one texture already reserved to start packing the next texture in the buffer more efficiently
    - This only happens if a texture's resolution is lower than the power-of-two resolution it has to reserve.
  - Multi-image texture indices: Multiple images stacked together. Clever layout of one texture's indices allow an image to change completely with only a palette swap.
- Each buffer shows the resolution, color depth, block count, and the number of textures inside it.

### Texture editor

- Editing a texture involves re-baking the buffer it lived in. For a single texture buffer it's simple enough,
  but a multi-texture buffer requires providing each 'layer' for a new buffer. This lets you employ
  the same optimization techniques in original buffers.
- Texture resolution can be changed in power-of-two steps. This must be done first before changing the color depth of a buffer,
  if your new image uses a higher color depth.
- Texture color depth can be changed, but only after the resolution matches the input image first, if it was changed.

### Car Variation editor
<p align="center">
  <img src="https://github.com/Silentwarior112/GTTexEdit/blob/main/docs/swatch-editor.png">
</p>

- The tool also contains a full suite to modify and add car variations (car colors):
  - Gran Turismo 3's Tex1-based CLUTs and Gran Turismo 4's external .pat systems are both supported
    via a unified UI that handles the technical differences under the hood. <br>
  - Duplicate variations, export the patched portions only or the full set as a swatch sheet with texture previews
    on the left, and the recolor-able swatches on the right side. <br>
  - Swatch editor: In your favorite image editor, recolor the swatches on the right, then save. <br>
  - Import swatch sheets to recolor a variation. <br>

  #### Recoloring technique for variations
  
  - After selecting the swatches:
      - `Black & White`: Required to perform a levels adjustment correctly.
      - Find the brightest swatch. This will be your input RGB.
      - If the color is black & relevant swatches are 0,0,0:
          - `Brightness` until the RGB is at least greater than 0,0,0.
          - After testing, come back here and fine-tune brightness / other adjustments you use
            until the car's color appears acceptable.
      - `Levels`. After plugging in the input RGB, set your desired output RGB value.
      - Save and import, test in-game.


### Car material editor
<p align="center">
  <img src="https://github.com/Silentwarior112/GTTexEdit/blob/main/docs/variation-editor.png">
</p>

  - Closely tied to the variation editor, this presents a spreadsheet of the car's material blocks.
  - Cars with variations have highlighted cells to indicate which materials change with variations.
  - 3 Presets available: Gloss, Metallic, Flat
      - Diffuse, Specular, and Specular power are the main parameters to focus on.
          - Diffuse = Brightness of the lowlight. Black cars typically get very low values, < 0.5
          - Specular = Brightness of the highlight. Metallic cars get values above 1.0
          - Specular power = Brightness difference magnitude & overall metallic appearance (~ 8 to 16)



## Technical Information

* [docs/internals.md](docs/internals.md) - what the files hold, what an edit reaches, and every feature in detail
