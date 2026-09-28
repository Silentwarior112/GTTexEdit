# How GTTexEdit works

The long version: what the files hold, what an edit reaches, and what every part of the tool does. See the
[README](../README.md) for what it is and how to run it, and `research/` for the measurements behind all of this.

## What it opens

It opens whatever holds textures and works out what that is for itself:

| | |
| --- | --- |
| GT4 car (`CAR4`) | models, colour patches and all, as `car_bin` holds them |
| course archive, GT3 or GT4 | a table of offsets; each slot is named and those with textures become parts |
| GT3 car archive | car info, model, tires and wheels - the textures in each are found |
| `MDLS` / `GTM1` on their own | a ModelSet2 or ModelSet1 outside any container |
| `Tex1` on its own | a bare texture set, as course archives carry them |

An archive slot that is not a magic of its own is searched for the ones it might be hiding, so the textures inside
a GT3 wheel file turn up without anything having to know what a GT3 wheel file is.

## A texture set is GS memory

A car texture set is one image of PS2 GS memory. Several pglu textures - **views** - usually read the SAME 4-bit
index data through palettes of their own, which is where the original files get their size from: 177,303 textures
across the game are only 128,746 buffers. GTTexEdit therefore shows a set the way the GS holds it, because that is
what decides who an edit reaches:

* editing a **palette entry** changes exactly one view (a CLUT slot has one user, and palette words never overlap
  pixels or another palette);
* editing a **texel** changes every view of its buffer.

Most edits change nothing about the layout: the GS memory, the block count and every file offset stay exactly as
they were, so an untouched part of a file is written back byte for byte. **Giving a texture a new size** and
**changing a buffer's colour depth** are the exceptions - the set is laid out again around the change, which is
what can make a file longer or shorter.

When a set that was laid out again no longer fits where it was, the **model is opened up behind it**: the bytes it
needs go in, everything after the cut moves down, and every pointer past it is given its new value from the model's
own relocation table, which is then written out again. So the file grows by what the edit needed - a 32x16 texture
going from 16 colours to 256 costs 1,279 bytes, not the 190,688 that a second copy of its texture set would - and
the set keeps its place in the model. A GT3 model has no relocation table, so it can only be made longer where its
set is the last thing in it; failing that, the set is still moved to the end the old way.

A buffer holds 16 colours or 256, and either can be made into the other. Nothing is fitted or approximated on the
way: the content is taken exactly as it is, and what the views of the buffer ask for between them either fits the
new depth or the whole change is refused with the number. Because the views share their texels, what counts is how
many distinct *combinations* of colours they want - not how many any one of them uses - so narrowing a buffer means
giving every view of it, in every variation, content that fits. The baker says that number as you work.

## Variations

A car's colour does not only tint. The same texels read through another palette are another
*picture*, so each colour is a whole alternative texture - that is how one 64x64 buffer on `subr0022` carries the
race numbers 77, 57, 87 and 97 while only its palette is swapped. GTTexEdit shows those as **variations** you switch
between and replace one at a time; replacing one leaves the others exactly as they were.

The two engines hold them differently, and the tool treats both as the same axis:

| | |
| --- | --- |
| GT4 | one palette, and a colour patch (`Pat0`) that holds different bytes for the words it covers |
| GT3 | every palette at once, and a **clut patch table** per variation that points each texture at one of them, plus a material array per variation |

A GT4 car's list of variations grows and shrinks on its own, because the patch is the list. A GT3 car's is a table
of pointers inside a model that declares no length of its own, so shortening and reordering happen in place, and
ADDING one is done by writing the longer tables at the end of the model and moving the pointers to them - nothing
already in the model moves a byte, and the archive around it is laid out again with its offset table rewritten.
A GT3 copy starts out reading the same palettes and the same materials as the variation it was copied from, which
is how the original cars are built: 161 of the 290 GT3 menu cars have two or more variations sharing one material
array. Edit its palettes, or give it materials of its own, to make it differ.

Edits that cannot mean anything per variation - a texel poke, a whole-buffer re-solve - still give up the GT4 patch
coverage they land on, because a patch describes the bytes that *were* there. What is left stays a valid patch and
keeps its paints, and the tool says how much it gave up.

## Using it

The window has two tabs: the **texture editor**, where a set is taken apart into buffers and views, and the
**variation editor**, which is the list of variations with the tools that change it, the sheets and the materials.

1. **File > Open** (or drop a file on the window). GT4 menu cars carry their colour patches inside; race cars
   (`car_bin\lod`) keep the main one beside them as `<name>.pat`, which is picked up automatically.
2. Pick a **set**. It is named by the part it belongs to and where it sits inside it: a GT4 car's LODs are
   `main:0.0` to `main:0.3`, a course archive's parts carry their slot names, and a GT3 course keeps its variations
   as whole sets of their own (`01 main course model:variation 1 set 10`). Then pick a **variation**: everything on
   screen - the picture, the palette, the joint table, the materials - is that variation.
3. The tree lists the set's **buffers**; a buffer read by more than one view is orange, and its views hang under it.
   Selecting a view shows it as the game draws it.
4. The **Palette** tab edits the selected view's colours - double-click a swatch for RGB, the Alpha box for alpha.
   A swatch framed white is one the variations switch; on a car that has them, an edit belongs to the variation on
   screen and the others keep the colour they had.
5. The **Joint table** tab is the buffer itself: one row per index value, one column per view. A texel picks a row
   and every view shows its own column of it. Rows marked `*` are used by no texel - free capacity for a new colour.
6. The **Buffer** tab says in words what an edit to this buffer would reach, and what growing it would cost.
7. **File > Import image into this view** takes a PNG of exactly the view's size. On variation 0 it snaps every
   texel onto the index whose joint colour fits best - *Import* chooses how hard the other views of the buffer are
   protected, *Dither* diffuses the remaining error - and leaves the palette alone. On a later variation it
   replaces what that variation shows, and asks how (see below).
8. Tick **Re-solve the buffer** to let the import re-derive the index image *and every view's palette at once*.
   That is the only way to give a shared buffer genuinely new colours: a texel of such a buffer has no colour of
   its own but one per view, so the index values are chosen by clustering those vectors. The views you did not
   replace keep what they show now, as strongly as *Import* asks.
9. **Give this view a new size** (Ctrl+R, or right-click a buffer) changes what a texture measures. It keeps the
   colours it has; the set is written out again with room for it, and whatever nothing reads any more is given
   back, so a texture can be resized over and over without the file growing every time. A view that was sharing
   its pixels with others stops sharing - they go on reading the old pixels, unchanged - and the dialog says so
   before anything happens, along with why a size is refused when it is.
10. **Export this buffer as a sheet** writes every view of a buffer stacked in one PNG; editing that file and using
   **Import a buffer sheet** replaces all of them in one re-solve.
11. **File > Save**. A GT4 race car is saved as the car plus its `.pat`.

### The variation editor

The second tab is the list of variations of the whole file, and everything that is about a variation rather than
about one texture. What it says at the top is which of the two mechanisms this file uses, how many of its palettes
the variations actually differ over, and - for GT3 - why *Add* is greyed out.

* **Add** appends a copy of the selected variation, **Remove** drops it, **Up** / **Down** reorder. The game picks
  one variation index for a whole car, so every part is kept in step: a GT4 car's patches all gain or lose a paint,
  and a GT3 car's clut patch tables and material table are all edited the same way - body model and wheel model
  alike. Removing a GT3 variation leaves its palettes and materials in the file, unreferenced, because nothing here
  moves or shortens a model; adding one makes the file a little longer (252 to 1,840 bytes, against the 819,200 a
  GT3 car may be), and the tool refuses rather than write a car past that.
* **Materials** that several variations share. Most GT3 cars point more than one variation at a single material
  array, so editing one really does edit the others - the tool says which, above the spreadsheet, and **Give this
  variation its own** copies the array so that it stops. That also makes the file longer, because the room left at
  the end of a GT3 model is 0 to 48 bytes and an array needs 240 to 1,600.
* **Sheets** are the whole file's palettes for ONE variation in a single PNG - GTPatEdit's paint sheet, for both
  engines. One band per palette: its identity markers at the left, thumbnails of up to four views that use it, then
  its colours as 24x24 swatches, 16 per row, each an opaque colour above a grey strip that is its alpha. Export it,
  paint over the swatches in any image editor, import it back. *The palettes that differ* is the short sheet (only
  what the variations disagree over); *every palette* is all of them. Import puts it into the variation on screen,
  or into a **new variation** copied from it first, so anything the sheet does not mention stays as it was. Bands
  are found by their markers rather than by position, so a sheet still imports after variations were removed or
  reordered, or when it was exported from another variation - leave the two small blocks at the left edge alone.
* **Materials** is the spreadsheet GTPatEdit has: a row per material of the part, a column per value of it (four
  RGBA colours as floats, then the specular power, flags as hex, and two more scalars). Type into a cell to set it;
  a tinted cell is one the variations give a value of its own. *Paint finish* applies *Gloss*, *Metallic / pearl* or
  *Metallic / pearl, no highlight* - what the original cars use - to every material you have a cell selected in.
  Which values are shown follows the variation: a GT4 car varies them through the same colour patch as its textures,
  while GT3 keeps a whole array per variation beside a base array that is none of them.

### Replacing one variation

Pick the variation, then import into it. Because the texels are shared by every variation, there are two ways to
hold the new picture, and the tool asks which, with the numbers for your image:

* **Palette only** - the texels stay shared, exactly as the original cars work, and the patch carries 16 or 256
  words. How close it gets depends on whether the shared index image happens to divide your picture up; the dialog
  states the error beforehand.
* **Palette and pixels** - the variation gets an index image of its own, so it is exactly your picture. The patch
  carries that too (4 KB for a 64x64 8-bit texture instead of 1 KB). Nothing moves either way: same size, same
  storage mode, same GS layout.

Replace one variation, switch to the next, replace that one. The rest of the car is untouched throughout.

### The buffer baker

Right-click a buffer (or Ctrl+B) to build its picture out of separate PNGs instead of one prepared file.

The canvas is the buffer as it already is, so its size is settled before you start: a layer larger than the canvas
is refused rather than scaled. The bake lands in the buffer's own palette unless **Colours** is used to change it.
**Add PNG** stacks layers, which are then placed by dragging them on the canvas or with the X / Y boxes, reordered
with **Up** / **Down**, and switched off with their tick. *Start from the picture it shows now* keeps the existing
texture as the bottom layer, so a badge or a stripe can simply be dropped on top of it.

A buffer read by several views gets one stack per view and per variation, chosen at the top; anything you give no
layers keeps what it shows. **Show it as it will bake** renders the real result for variation 0 - the same solve the
bake runs, in the palette all the views have to share - so nothing is a surprise. **Bake into the buffer** then
commits variation 0 as one joint re-solve and each later variation as a replacement, asking once how those should
be held.

**Colours** is where that settled palette size changes: *Keep its 16 colours* or *Make it 256 colours*, and the
other way round. It is a different kind of bake, so the controls that fit content to a palette grey out - nothing
is fitted, and the canvas is then exactly what would go in. The status line keeps a running count of how many
colours the buffer's pictures need between them against the depth you chose, and the bake writes **every** view in
**every** variation: the ones you gave layers from the layers, the rest from what they already show. Content that
does not fit is refused with the number rather than approximated down, so narrowing a buffer means adding layers
until it does. A buffer that cannot change depth at all - views that read it in different ways, a window into it,
true colour - says which of those it is when you ask. The set is laid out again around the new depth, so the file
may get longer or shorter.

## Layout

```
src/GTTexEdit.Core/
  Foundation/    RgbaImage, ByteReader, ByteWriter            (shared with GTPatEdit)
  Imaging/       Png, Quantizer (one image), JointQuantizer (a buffer read by N views at once), Compositor (layers)
  Gs/            GS memory emulation, Tex1Reader, PgluTexture  (shared) + Tex1Analysis (who reads which nibble)
                 + GsLayout (the set decomposed into buffers and the views that share them)
                 + Tex1Builder (a texture set written out from scratch - registers, memory, clut patch sets)
  Cars/          Car4File, Pat0 (+ Uncover: giving coverage up where an edit lands on it)
  Containers/    ContentScanner (what a file is, and what is inside it - archives, models, bare texture sets),
                 ArchiveWriter (laying an offset-table archive out again when a constituent changed length)
  Models/        ModelSetTextures (the texture sets of a ModelSet2 or a ModelSet1, variations included),
                 ModelMaterials (PGLUmaterial, the same 0x50 bytes in both engines, and the paint finishes),
                 ModelRelocations (a ModelSet2's table of its own pointers - and opening a model up in the middle,
                 so something inside it can get longer where it already is)
  Editing/       EditableModel (variation-aware reads and writes, or dropping patch coverage), TextureSet (buffers,
                 views, palette and texel edits, import, joint re-quantisation, per-variation replacement),
                 SetRebuilder (a set written out again, and the proof it shows what it showed), SetAllocator
                 (moving blocks: reclaiming what nothing reads, finding room for a texture that grew, and laying
                 a buffer out again at another colour depth), DepthChange (what a set of pictures needs between
                 them, solved exactly - no quantiser),
                 TextureDocument (open anything / save it back), VariationTables (reordering and removing a GT3
                 car's variations in place), VariationBuilder (adding one: the longer tables written at the end of
                 the model), VariationSheet (every palette of one variation as one PNG)
src/GTTexEdit/   the WinForms app (MainForm, BufferBakerForm, ResizeDialog, VariationFitDialog, Controls/)
tools/TexDbg/    debug console tool
tests/           console test runner; tests/ui holds screenshot scripts (screenshot.ps1, bake_shot.ps1)
docs/            this file and the README's screenshots
research/        BRIEF.md (research brief), FINDINGS.md (the format), GS_EDIT_MODEL.md (buffers, the joint
                 palette table, what an edit reaches, what growth costs), TOOL_DESIGN.md, notes/ (one file per
                 topic, every number in them measured over every car or course), gslib.py (Python port of the
                 GS layout, checked byte for byte against TexDbg)
```

The per-topic working directories those notes were produced from - extracted textures, GS dumps, per-car CSV
surveys, the scripts that made them - are about a gigabyte of data derived from the game's own files, so they
are not part of the repository. The findings are.

## What the tests check

The runner is `tests\GTTexEdit.Tests` (see the [README](../README.md) for how to run it).

The tests need real car files, which cannot be redistributed; without them the run reports SKIPPED. The folders
default to the `GTTEXEDIT_CARS` and `GTTEXEDIT_GT3CARS` environment variables; course archives are picked up from
`crs_bin` beside `car_bin`. The GT3 folder wants the **menu** cars (`gt3_vol\data\menu\cars`): the race-time models
beside them keep a whole texture set per variation, which the tool declines to add variations to, so they fail
checks that were never about them. Against the full set of 1,612 GT4 cars, 10 course archives and a sample of GT3
menu cars (95 checks, about five minutes):

* every car is reopened and saved byte for byte, `.pat` included: 8,053 models, 19,306 sets, 128,746 buffers,
  177,303 views;
* `Pat0.Uncover` splits a target cleanly, leaves ascending non-overlapping targets, keeps every paint and still
  round-trips;
* palette edits reach one view; texel edits reach every view of their buffer; both survive a save;
* re-importing a view's current picture changes nothing at all, and gives up no patch coverage;
* "lock the other views" leaves the other views of a shared buffer byte-identical;
* re-solving a private buffer reproduces the image, and re-solving a shared one matches the replaced view while the
  others stay close to what they showed;
* the bake preview is exactly, pixel for pixel, what the bake then produces;
* the compositor blends straight alpha, clips at the edges and leaves the rest of the canvas alone;
* replacing one variation gets as close as its palette size allows, leaves every other variation byte-identical,
  keeps the layout, and survives a save; a palette edit belongs to its variation alone; and the palette-only error
  the tool predicts beforehand is the one that comes out;
* every course archive beside `car_bin` in `crs_bin` is recognised, opens, decodes every view of every constituent,
  saves byte for byte, and takes an edit that survives a save without changing the file's length;
* every buffer of every archive gives up its joint table and every palette entry of every view - including the
  four buffers read at two colour depths at once, where a view that reads the buffer 4-bit has 16 entries against
  an 8-bit view's 256 and simply has nothing in the rows above;
* material values read back, can be set, belong to the variation they were set in, survive a save, and a paint
  finish sets diffuse, specular and the power without touching anything else;
* adding, moving and removing a variation keeps every part of a car in step and survives a save;
* a variation sheet imported back unchanged changes nothing, a repainted swatch reaches the variation it was
  imported into and no other, the short sheet is a subset of the full one, and a PNG that is not a sheet - or one
  whose markers were painted over - is refused;
* a GT3 car's variations are read from its clut patch tables, really are different pictures, and take a palette edit
  that stays in the variation it was made in; moving two swaps what they show; removing one shifts the rest down,
  takes its material array with it, changes no length, and survives a save with the archive exactly as long as it
  was; adding one shows what it was copied from, leaves every other variation byte-identical, keeps the GS layout,
  and comes back the same after a save into the now-longer archive;
* variations that share a material array are told apart, an edit through one really does reach the others, giving
  one an array of its own separates it and costs exactly one array, and the longer model saves and reopens with
  every other constituent byte-identical;
* rewriting an archive that nothing changed gives back the same bytes, and growing a constituent of it moves the
  later ones without disturbing a byte of them;
* a texture set written out again shows exactly what it showed, over 868 sets of both engines - 309 of them come
  back byte for byte identical, and the rest only differ in the slack the originals happened to leave;
* squeezing out what nothing reads shows exactly what it showed, and giving one texture a new size gives it that
  size, reaches nothing else in the file, and is still there after a save and reopen;
* every relocation table of every ModelSet2 - 8,048 of them - is written out again byte for byte, its pointers
  ascend and lie in the body, and not one of them lives inside a texture set;
* opening a model up leaves every pointer in it pointing at exactly the bytes it pointed at before, over 125
  models; a set that grows stays where it is, the model grows by what it needed rather than by a copy of the set,
  the model keeps its shape (header, pointers, sets on 0x80, none overlapping) and every other texture set in it
  shows exactly what it showed - before and after a save and reopen;
* a GT3 set with nothing to switch between keeps the empty clut patch set it came with, the way the originals
  leave it, rather than gaining a patch that only repeats what the registers already say;
* every view still knows where its own palette is after a set has moved, a colour written into a set that moved
  lands in that set and reaches no other, and the room a set has to grow into never reaches over anything but the
  zeros behind it - measured on every set of every GT3 car, since exactly one model in the 861 would catch it;
* a buffer given a deeper palette shows exactly what it showed - every view, in every variation, before and after a
  save and reopen - and shows it still when it is taken back down again; content needing more colours than the depth
  holds is refused, and the refusal says how many it needs;
* no other edit ever changes a layout, and giving coverage up barely changes a file's size.

## TexDbg

```
dotnet build tools\TexDbg\TexDbg.csproj -c Release
tools\TexDbg\bin\Release\net10.0\TexDbg.exe <command>
```

| command | what it gives |
| --- | --- |
| `list <car>` | the model parts of a car and their populated texture sets (`part:list.slot`) |
| `dump <car> [--part P] [--list L] [--set S] [--blocks]` | per set: header, GS transfers, every texture with all GS register fields, distinct CLUT slots, clut patches, memory sharing between textures, block totals against two baselines, colour patch coverage, file layout with gaps; `--blocks` adds the block ownership map |
| `gs <car> [--part P] [--list L] [--set S] [--table]` | **the set as the GS holds it**: buffers (shared index images) with the views that read each, block accounting, and with `--table` the joint palette table - one row per index value, one column per view |
| `gssheet <car> <outDir> [...]` | one PNG per buffer: its index image under every view's palette, stacked, plus `gsview.txt` |
| `blocks <car> --part P --set S [--from A] [--to B]` | block ownership map only, equal neighbours merged |
| `json <car> [-o file]` | everything above as JSON |
| `extract <car> <outDir> [...]` | per set: `tNN.png`, `tNN.idx` (one byte per visible texel), `tNN.pal` (GS palette words in index order), `gs.bin` (GS memory in block order), `tex1.bin`, `set.json`, `report.txt`, `atlas_t4.png` / `owners.png` |
| `survey <outDir> <folder>...` | CSV tables over every car below the folders: `parts`, `sets`, `textures`, `overlaps`, `cluts`, `transfers` (all 1,613 cars take about ten seconds) |

Units: 1 GS block = 64 words = 256 bytes = 512 nibbles; a PSMT4 texel is one nibble, PSMT8 two, PSMCT32 eight.

`research/gslib.py` is a Python port of the same memory layout (checked byte for byte against TexDbg's output),
for ad-hoc scripts: `Car(path).part('main').sets[0].tex1` exposes the register tables, the emulated GS memory,
`indices(i)`, `palette(i)`, `word_source` (GS word -> file offset) and the raw address functions.

Parts of the GS / Tex1 code are ported from PDTools; see [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
