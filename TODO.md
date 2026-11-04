# TODO

## GPU fan rings (RTX 5070 Ti Gaming OC, PCI 10DE:2C05 sub 1458:4181)
- The card has a side light bar and RGB Halo rings on its 3 fans.
- The public protocol (I2C 0x75, registers 0x16 live color and 0x12 stored effect) reaches the side bar only. `identify gpu` tried hardware zones 0-7 on both registers; no fan ring lit.
- Other owners of 50-series Gaming OC cards report the same: the logo works, the fans do not.
- Next step: record the NVAPI I2C calls that GIGABYTE Control Center makes when it sets a fan color, for example with an NVAPI call hook. Then add the fan zones to `GigabyteGpuDevice`.

## Motherboard RGB near the RAM / chipset shroud (B850 AORUS ELITE WIFI7 ICE)
- Reported (unverified, 2026-11-04): some B850 AORUS ELITE boards have a short onboard RGB strip near the RAM cover or chipset/NVMe shroud, controllable from RGB Fusion in GIGABYTE Control Center.
- So far only Fusion 2 firmware zone 2 (the board-bottom LEDs) has lit; see `OnboardZones` in rgbblaster.json.
- Next step: run `identify walk onboard --all-zones` and watch the RAM and chipset area for each zone. If none lights it, set its color in GCC and diff the Fusion 2 reports (zones 0-10, both 0x20-0x27 and 0x90-0x92 command ranges).
