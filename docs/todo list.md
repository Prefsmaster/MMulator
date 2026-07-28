# Hardware

* Interne FD + Mem kaart + (max 2) drives?
* Joystick
* Serial Port Cartridge
* M2200 FD, Mem kaart, poorten en clock
* Multi Rom cartridges (dip/rotary switch selectie)
* terugspoel dinges
* TAPE turbo load (machine must intercept rom calls and copy cassette header+payload to emulator RAM)

# Software

* Printer device aansluiten op P2000, ruwe output naar txt, pdf, matrix printer driver?
* Cassette window meer grafisch
* IDE integratie
* Screenshot: shortcut-save: output path en naam pattern kunnen aanpassen.
* Shortcuts tonen in menu's
* Memory windows cursortoetsen; Mouse over show dec, hex binary & exacte adres
* Memory windows editable, per cell maar ook range select, copy paste(?)
* 'quick save state' of the machine, for undo when developing or playing games
* Reset Config in config window
* Better way for selecting startup config (SET, path)

# UI

* Show read/write/bytes transferred in disk tabs.
* show position of tape in cass window.
* disk contents in UI, PDOS/JWSDos can be parsed, if none found it is probably a system disk, make guess of what system based on magic byte(s)
* Debug disassembly can be confusing add switch: continuous/only when paused/on breakpoint.
* Debug window: processor flags hard to read now. Change to Header with flag names/letters and 1/0 below it.

# Bugs

* Warm/Cold reset na Ghosthunt/U Hangt: geen keyboard input.
* Display glitches in Ghosthunt kloppen niet.
* test csave replace/append: gaat nog niet helemaal goed.
