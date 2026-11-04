# TODO

## GPU fan rings (RTX 5070 Ti Gaming OC, PCI 10DE:2C05 sub 1458:4181)
- The card has a side light bar and RGB Halo rings on its 3 fans.
- The public protocol (I2C 0x75, registers 0x16 live color and 0x12 stored effect) reaches the side bar only. `identify gpu` tried hardware zones 0-7 on both registers; no fan ring lit.
- Other owners of 50-series Gaming OC cards report the same: the logo works, the fans do not.
- Next step: record the NVAPI I2C calls that GIGABYTE Control Center makes when it sets a fan color, for example with an NVAPI call hook. Then add the fan zones to `GigabyteGpuDevice`.
