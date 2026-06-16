# HamMeter

A minimalist, easy-to-read combat tracker and damage meter for **Final Fantasy XIV**, built as a [Dalamud](https://github.com/goatcorp/Dalamud) plugin. It reads encounter data from **IINACT** over Dalamud IPC and presents it in a clean, modern, fully customizable overlay.

> **Requires [IINACT](https://github.com/marzent/IINACT)** to be installed and running. HamMeter does not parse the game itself — it reads the encounter data that IINACT provides.

## Features

- Clean, modern overlay with rounded bars and a customizable header
- Metrics: Damage Done, Damage Taken, Healing Done, Healing Taken, Deaths
- Current encounter / Overall / per-encounter history
- Job icons and per-job or per-role colors (fully editable)
- Robust auto-reset when entering instanced content
- Test mode to preview the layout without being in combat
- Extensive settings: sizes, spacing, opacity, colors, and more

## Installation

HamMeter is distributed through a custom Dalamud plugin repository (damage meters are not eligible for the official repository).

1. In game, open the Dalamud settings with `/xlsettings`.
2. Go to the **Experimental** tab.
3. Under **Custom Plugin Repositories**, add this URL:

   ```
   https://raw.githubusercontent.com/NennMichSchinken/HamMeter/main/repo.json
   ```

4. Click the **+** to add it, then **Save**.
5. Open the plugin installer with `/xlplugins`, search for **HamMeter**, and install it.
6. Make sure **IINACT** is installed and running.

## Usage

- `/hammeter` (or `/ham`) — toggle the meter
- `/hammeter config` — open the settings
- `/hammeter test` — toggle test mode
- `/hammeter clear` — reset the current data
- `/hammeter end` — end the current IINACT encounter

## License

Released under the MIT License. See [LICENSE](LICENSE).
