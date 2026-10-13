# EPS: power bus and battery

> Fictional runbook for the synthetic SYNTH-01 demo vehicle. Not an operational procedure for any real system.

## Bus undervoltage

The main bus is nominally 28 V. A sustained drop below 26.5 V is an undervoltage condition. A sudden step
down usually means a load fault, a converter dropping out, or loss of a power source; a slow sag suggests
battery depletion.

- Compare bus voltage with battery temperature and current: a hot battery plus low bus voltage suggests a high-current load fault.
- Identify loads switched on in the 10 s before the voltage step (command history).
- Check whether downstream subsystems (comms, thermal control) degraded after the bus moved.
- If voltage stays below 26.5 V, prepare a load-shed of non-essential loads per the power budget.

## Battery temperature rise under load

Battery temperature climbing while the bus voltage is low indicates high discharge current. Expect roughly
linear heating while the load persists.

- Correlate the battery temperature trend with bus voltage and the start of the load.
- Verify the battery heater is not commanded on.
- Watch for the temperature trend continuing after loads are shed.
