# GNC: attitude rate

> Fictional runbook for the synthetic SYNTH-01 demo vehicle. Not an operational procedure for any real system.

## Attitude rate transient

Attitude rate is nominally near 0 deg/s. A real rate change persists for many samples and shows up on the
attitude estimate; a one-sample jump that returns immediately is usually a gyro or data glitch.

- Compare the gyro rate with the attitude estimate: a real rate change moves the attitude.
- Check for thruster or reaction wheel commands at the same time.
- Check other gyro axes for a simultaneous jump.
