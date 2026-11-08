# App icon

The mark is an **L** with three fading rows: `larik` means row / line (and is used for
*array* in Indonesian), and the rows read as table records in a SQL studio. The letterform
is tuned to stay legible at 16 px.

- `AppIcon.svg` — 1024 px master (source of truth)
- `../Assets/AppIcon.ico` — multi-resolution icon embedded in `Larik.exe`
- `../Assets/Square*`, `StoreLogo`, `Wide*`, `Splash*`, `LockScreen*` — MSIX assets

## Regenerate

From the repository root:

```powershell
winapp manifest update-assets Larik\Branding\AppIcon.svg --manifest Larik\Package.appxmanifest
Copy-Item Larik\Assets\Square44x44Logo.targetsize-48_altform-unplated.png `
          Larik\Assets\Square44x44Logo.targetsize-48_altform-lightunplated.png -Force
```

The manifest only references the 200 % assets, so the extra scale and target-size variants
that winapp generates can be deleted afterwards. winapp also scales the square master to
fill the wide tile and splash; the shipped versions use a smaller centered icon:

```powershell
# wide tile: 220 px icon in 620x300
magick -background none Larik\Branding\AppIcon.svg -resize 220x220 wide.png
magick -size 620x300 xc:none wide.png -gravity center -composite `
       PNG32:Larik\Assets\Wide310x150Logo.scale-200.png

# splash: 435 px icon in 1240x600
magick -background none Larik\Branding\AppIcon.svg -resize 435x435 splash.png
magick -size 1240x600 xc:none splash.png -gravity center -composite `
       PNG32:Larik\Assets\SplashScreen.scale-200.png

# lock screen logo (not declared in the manifest)
magick -background none Larik\Branding\AppIcon.svg -resize 48x48 `
       PNG32:Larik\Assets\LockScreenLogo.scale-200.png
```
