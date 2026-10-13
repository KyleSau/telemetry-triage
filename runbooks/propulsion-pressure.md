# Propulsion: tank pressure

> Fictional runbook for the synthetic SYNTH-01 demo vehicle. Not an operational procedure for any real system.

## Tank pressure excursion

Tank pressure is nominally 2400 kPa with roughly 2 kPa of sensor noise. A real pressure change is smooth
and is usually accompanied by a temperature change.

- Compare against the redundant pressure transducer before acting on a single sensor.
- Check valve command history for the 30 s before the excursion.
- Check tank temperature for a matching trend.

## Pressure sensor frozen or disagreeing

A pressure reading that stops changing while every other channel is still noisy is a sensor or data-path
fault, not a physical event. Pressure is effectively unobserved until the sensor recovers.

- Confirm the frozen value against the redundant transducer.
- Check the sensor's data-acquisition card and its other channels for the same symptom.
- Treat pressure as unknown and hold any propulsion operations until a trusted reading is available.
