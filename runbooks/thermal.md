# Thermal: avionics temperature

> Fictional runbook for the synthetic SYNTH-01 demo vehicle. Not an operational procedure for any real system.

## Avionics temperature drift

A slow upward drift in avionics temperature can be a real thermal trend (heater stuck on, radiator blocked,
higher electrical load) or a calibration drift in the sensor. Rolling-window detectors miss slow drift; the
EWMA drift detector compares against the pre-incident baseline instead.

- Compare with neighbouring temperature sensors: a real trend shows up on more than one.
- Check heater command states and duty cycle over the drift period.
- Estimate time to the upper operating limit from the trend and the baseline.

## Temperature sensor dropout

A missing temperature channel removes observability but is rarely urgent on its own.

- Check other sensors on the same harness for dropouts.
- Fall back to neighbouring sensors to bound the temperature.
