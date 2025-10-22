// Normalize pointer/mouse event data. Prefer modern PointerEvent properties.
// Usage: const { pressure, pointerType } = normalizePointerEvent(evt);
export function normalizePointerEvent(evt) {
  // pressure: PointerEvent.pressure exists on pointer events (0..1).
  // Some older browsers expose vendor properties; avoid reading them directly to prevent deprecation warnings.
  const pressure = (evt && typeof evt.pressure === 'number')
    ? evt.pressure
    : (evt && typeof evt.force === 'number' ? evt.force : 0);

  // pointerType: prefer PointerEvent.pointerType when available.
  // Fallback to mouse for legacy MouseEvent.
  const pointerType = (evt && typeof evt.pointerType === 'string')
    ? evt.pointerType
    : 'mouse';

  return { pressure, pointerType };
}