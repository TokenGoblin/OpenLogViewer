#!/bin/bash
# Regenerates the reference screenshots from the C# app (branch retro-theme).
# Build it first, outside the C# folder so a running copy does not lock it:
#   dotnet build <OpenLogViewer>/src/OpenLogViewer.App/OpenLogViewer.App.csproj -p:OutDir=<this repo>/tests/screens-build/
# Existing PNGs are kept; delete one to capture it again. Runs the agent API on port 18765.
# The log path goes first on every run: the C# reads the first bare argument as a log,
# so a switch whose value is optional (--guide, --tuning-project) or whose output path
# is a second value (--calculators, --top-menu) would otherwise open the .png as a log.
EXE=${OLV_EXE:-$(cd "$(dirname "$0")/../../.." && pwd)/tests/screens-build/OpenLogViewer.App.exe}
OUT=$(cd "$(dirname "$0")" && pwd -W 2>/dev/null || pwd)
LOG=${1:?usage: capture.sh <log.mlg> <tune.msq>}
MSQ=${2:?usage: capture.sh <log.mlg> <tune.msq>}
run() { name=$1; shift; [ -f "$OUT/$name.png" ] && { echo "$name exists"; return; }; timeout 60 "$EXE" "$LOG" "$@" --agent-api 18765 >/dev/null 2>&1; echo "$name rc=$? $( [ -f "$OUT/$name.png" ] && echo ok || echo MISSING)"; }
shot() { name=$1; shift; run "$name" "$@" --screenshot "$OUT/$name.png"; }
for theme in midnight daylight retro; do
  shot log-$theme --theme $theme
  shot histogram-$theme --histogram --theme $theme
  shot scatter-$theme --scatter --theme $theme
  shot gauges-$theme --gauges --theme $theme
  shot calibration-noecu-$theme --calibration VE --theme $theme
  shot calibration-settings-$theme --open-tune "$MSQ" --page Injector --theme $theme
  shot guide-$theme --guide "Getting started" --theme $theme
done
shot log-retro-150 --theme retro --zoom 1.5
shot histogram-retro-150 --histogram --theme retro --zoom 1.5
shot scatter-retro-150 --scatter --theme retro --zoom 1.5
shot gauges-retro-150 --gauges --theme retro --zoom 1.5
shot calibration-noecu-retro-150 --calibration VE --theme retro --zoom 1.5
shot calibration-settings-retro-150 --open-tune "$MSQ" --page Injector --theme retro --zoom 1.5
shot guide-retro-150 --guide "Getting started" --theme retro --zoom 1.5
shot log-pointer-midnight --pointer 0.5,0.5 --theme midnight
shot log-find-midnight --find "RPM > 4000" --theme midnight
shot log-stacked-midnight --stacked --theme midnight
shot histogram-ve-midnight --histogram --ve --ve-values --theme midnight
shot insights-midnight --insights --theme midnight
shot project-midnight --tuning-project --theme midnight
for t in "Engine recipe" "Boost" "Pressure ratio" "Turbo sizing" "Airflow" "Intercooling" "Injectors" "Fuel pump" "Lambda" "Octane" "Engine" "Runners & headers" "Gearing" "Drag strip" "Fuel economy"; do
  slug=$(echo "$t" | tr 'A-Z' 'a-z' | sed 's/ & /-/; s/ /-/g')
  run calculators-$slug-midnight --theme midnight --calculators "$t" "$OUT/calculators-$slug-midnight.png"
done
run power-midnight --theme midnight --power "$OUT/power-midnight.png"
run faults-midnight --theme midnight --faults "$OUT/faults-midnight.png"
for m in File View Tools Help; do
  l=$(echo $m | tr 'A-Z' 'a-z'); run menu-$l-midnight --theme midnight --top-menu $m "$OUT/menu-$l-midnight.png"
done
run menu-view-retro-150 --theme retro --zoom 1.5 --top-menu View "$OUT/menu-view-retro-150.png"
run menu-connect-midnight --theme midnight --menu "$OUT/menu-connect-midnight.png"
echo ALLDONE
