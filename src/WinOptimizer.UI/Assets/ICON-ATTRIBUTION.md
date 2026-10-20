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
win-optimizer. `FeatureIcons.xaml` owns the navigation paths; the Custom paths
and their semantic name lookup are generated from `extras/generate-icons.py`.
Filled outlines inherit the host's foreground color, including theme and
high-contrast changes. The project's license applies to these drawings.

Licenses and attributions for Fluent symbols and installed-program logos are in
[THIRD-PARTY-NOTICES.txt](../../../THIRD-PARTY-NOTICES.txt).
