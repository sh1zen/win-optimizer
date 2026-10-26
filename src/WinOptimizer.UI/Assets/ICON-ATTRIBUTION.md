# Image assets and attribution

The application identity uses the user-supplied blue geometric logo. Its original
ownership and licensing terms apply.

| Asset under `AppIcons/` | Purpose |
| --- | --- |
| `logo-source.png` | Original artwork |
| `win-optimizer.png` | Interface tile; rounded neutral perimeter, original colored pixels |
| `win-optimizer.ico` | Executable, window and installer icon; 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel frames |

Shortcuts, file associations and the uninstall entry use the executable's embedded
icon. [Build the app](../../../README.md#development) to copy current assets and
embed the ICO.

## Interface artwork

Navigation and the Custom icon pack use original 24-unit drawings authored for
win-optimizer. Navigation paths are authored in
`Features/Common/Resources/FeatureIcons.xaml`; the Custom drawing definitions and
semantic aliases are authored in `extras/generate-icons.py`. The generator preserves
navigation/symbol paths and writes Custom paths into the same resource dictionary,
with name lookup in `CustomIconCatalog.cs`.

Run from the repository root with Python's standard library:

```powershell
python extras/generate-icons.py
python extras/generate-icons.py --check
```

The first command updates the generated resources; `--check` verifies them without
writing. Navigation strokes and Custom filled outlines inherit the host's foreground
color, including theme and high-contrast changes. Explicit Fluent symbols use the
declared icon library. The project's license applies to the original drawings.

Licenses and attributions for Fluent symbols and installed-program logos are in
[THIRD-PARTY-NOTICES.txt](../../../THIRD-PARTY-NOTICES.txt).
