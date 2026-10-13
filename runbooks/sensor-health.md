# Sensor health and data quality

> Fictional runbook for the synthetic SYNTH-01 demo vehicle. Not an operational procedure for any real system.

## Stuck or frozen sensor values

A healthy sensor always shows some noise. The same value repeated many samples in a row means the sensor,
its data-acquisition card, or the telemetry encoder is frozen.

- Check whether other channels from the same data-acquisition card are also frozen.
- Compare with a redundant sensor if one exists.
- Mark the channel as untrusted in displays until it shows noise again.

## Channel dropout and missing data

A channel missing from telemetry frames while the rest of the frame arrives normally points to the sensor,
its harness, or the telemetry encoding rather than the downlink.

- Check whether the dropout is isolated to one channel or one data-acquisition card.
- Check the telemetry format or encoding configuration for recent changes.
- Record the gap so downstream analytics do not interpolate across it.

## Single-sample spikes

A one-sample jump that immediately returns to baseline is most often a single-event upset, an electrical
transient, or a decoding glitch.

- Check for simultaneous spikes on other channels (shared electrical cause).
- Confirm the value returned to baseline and stays there.
- Log the event; act only if spikes repeat.
