# TODO

## GPU fan rings (RTX 5070 Ti Gaming OC, PCI 10DE:2C05 sub 1458:4181)
- The rings light only while the GPU fans spin. At idle the card stops its fans, so the rings are dark.
- GIGABYTE Control Center offers no separate ring control. A capture of its NVAPI I2C calls shows one write per color change: address 0x75, register 0x16, zone 0.
- Open decision: hold a minimum GPU fan duty (through FanController) so the rings stay lit, and hand the fans back to the card on exit.
