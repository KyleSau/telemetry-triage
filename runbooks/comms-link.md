# Comms: RF link quality

> Fictional runbook for the synthetic SYNTH-01 demo vehicle. Not an operational procedure for any real system.

## Link SNR degradation

Link SNR is nominally 18 dB. A step drop of several dB that coincides with a power event usually means the
transmitter output dropped with the bus voltage, not an RF or ground-station problem.

- Check whether the SNR drop lines up with a bus voltage change in the same incident.
- Check ground-station antenna pointing and weather before blaming the vehicle.
- Confirm the link margin still supports the current data rate.

## Ground-station side issues

- Check the ground-station receiver logs for the same time window.
- Compare against a second ground station if one is in view.
