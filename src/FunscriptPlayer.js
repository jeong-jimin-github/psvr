/**
 * Funscript parser and local position calculator.
 * Funscript format: { version, actions: [{ at: ms, pos: 0-100 }, ...] }
 */
export class FunscriptPlayer {
  constructor() {
    this.actions = [];
    this.loaded = false;
  }

  load(jsonData) {
    const data = typeof jsonData === 'string' ? JSON.parse(jsonData) : jsonData;

    if (!data.actions || !Array.isArray(data.actions)) {
      throw new Error('Invalid funscript: missing actions array');
    }

    // Sort by time ascending
    this.actions = [...data.actions].sort((a, b) => a.at - b.at);
    this.loaded = true;

    return {
      duration: this.actions.length > 0 ? this.actions[this.actions.length - 1].at : 0,
      actionCount: this.actions.length,
    };
  }

  clear() {
    this.actions = [];
    this.loaded = false;
  }

  /**
   * Get interpolated position (0-100) at given time (milliseconds).
   */
  getPositionAt(timeMsec) {
    if (!this.loaded || this.actions.length === 0) return 0;

    const actions = this.actions;

    if (timeMsec <= actions[0].at) return actions[0].pos;
    if (timeMsec >= actions[actions.length - 1].at) return actions[actions.length - 1].pos;

    // Binary search for surrounding keyframes
    let lo = 0;
    let hi = actions.length - 1;
    while (lo < hi - 1) {
      const mid = (lo + hi) >> 1;
      if (actions[mid].at <= timeMsec) lo = mid;
      else hi = mid;
    }

    const a = actions[lo];
    const b = actions[hi];
    const t = (timeMsec - a.at) / (b.at - a.at);
    return Math.round(a.pos + (b.pos - a.pos) * t);
  }
}
