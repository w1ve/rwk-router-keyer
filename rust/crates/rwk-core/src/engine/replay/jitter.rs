//! Jitter buffer delay selection.
//!
//! Port of `RWK.Station.Replay.JitterBuffer` and `EdgeJitterProfile`.
//!
//! The buffer chooses the playout delay `D` added to an arriving edge:
//!
//! * **Bands.** Direct path 30-150 ms (configured default 60 ms); DERP-class 100-500 ms
//!   (default 200 ms). The configured base delays are clamped into their band, so a
//!   configuration outside the range cannot yield an out-of-range delay.
//! * **Profile is an input, not an assumption.** The sidecar declares
//!   `edge.jitterProfile`; [`EdgeJitterProfile::DerpClassOnly`] forces the DERP band at
//!   all times. An unknown declaration resolves to the conservative profile, because a
//!   longer buffer costs latency while a shorter one costs timing fidelity.
//! * **Adaptation.** `delay = base + 2 * jitter_ewma`, clamped to the band, where the
//!   jitter sample is the absolute deviation of each RTT from the RTT EWMA as it stood
//!   before that sample. Before the first sample the base delay is used unchanged.
//! * **Late-edge storm.** More than three late edges inside a 10 s window auto-bumps the
//!   delay one 10 ms step, so transient degradation is compensated without waiting for
//!   the EWMA to drift up.
//!
//! The replayer owns one instance and mutates it from the replay thread; getters are
//! plain reads. Nothing on the keying path blocks on a lock.

use std::collections::VecDeque;
use std::time::Duration;

use crate::primitives::PathType;

/// How much freedom the Station has when choosing its jitter delay.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum EdgeJitterProfile {
    /// Pick the band from the observed path type.
    PathAdaptive = 0,
    /// Use the DERP-class band at all times.
    #[default]
    DerpClassOnly = 1,
}

impl EdgeJitterProfile {
    /// The sidecar's string for [`Self::PathAdaptive`].
    pub const PATH_ADAPTIVE: &'static str = "PathAdaptive";
    /// The sidecar's string for [`Self::DerpClassOnly`].
    pub const DERP_CLASS_ONLY: &'static str = "DerpClassOnly";

    /// Parses a declared profile; unrecognized, empty or missing resolves to the
    /// conservative [`Self::DerpClassOnly`].
    #[must_use]
    pub fn from_declaration(declaration: Option<&str>) -> Self {
        match declaration {
            Some(Self::PATH_ADAPTIVE) => Self::PathAdaptive,
            _ => Self::DerpClassOnly,
        }
    }
}

/// Base delays and adaptive mode.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct JitterBufferConfig {
    /// Buffer delay on a direct path.
    pub direct_delay: Duration,
    /// Buffer delay on a DERP-relayed path.
    pub derp_delay: Duration,
    /// When true, the delay adapts to measured RTT and jitter.
    pub adaptive_mode: bool,
}

impl Default for JitterBufferConfig {
    fn default() -> Self {
        Self {
            direct_delay: Duration::from_millis(crate::primitives::limits::DEFAULT_DIRECT_JITTER_MS),
            derp_delay: Duration::from_millis(crate::primitives::limits::DEFAULT_DERP_JITTER_MS),
            adaptive_mode: true,
        }
    }
}

/// Chooses the playout delay for an arriving edge.
#[derive(Debug, Clone)]
pub struct JitterBuffer {
    config: JitterBufferConfig,
    profile: EdgeJitterProfile,
    path: PathType,
    pending_path: Option<PathType>,
    rtt_ewma_ms: f64,
    jitter_ewma_ms: f64,
    has_samples: bool,
    auto_bump_ms: f64,
    late_edge_ms: VecDeque<u64>,
    current_delay: Duration,
}

impl JitterBuffer {
    /// Shortest delay permitted on a direct path.
    pub const DIRECT_MIN: Duration = Duration::from_millis(30);
    /// Longest delay permitted on a direct path.
    pub const DIRECT_MAX: Duration = Duration::from_millis(300);
    /// Shortest delay permitted on a DERP-class path.
    pub const DERP_MIN: Duration = Duration::from_millis(100);
    /// Longest delay permitted on a DERP-class path.
    pub const DERP_MAX: Duration = Duration::from_millis(500);
    /// EWMA smoothing factor for RTT samples.
    pub const RTT_ALPHA: f64 = 0.2;
    /// EWMA smoothing factor for jitter samples.
    pub const JITTER_ALPHA: f64 = 0.1;
    /// Multiplier applied to the jitter EWMA by the adaptive formula.
    pub const JITTER_MULTIPLIER: f64 = 2.0;
    /// Late edges within the storm window that trigger an auto-bump.
    pub const LATE_STORM_THRESHOLD: usize = 3;
    /// Sliding window for late-edge storm detection, in milliseconds.
    pub const LATE_STORM_WINDOW_MS: u64 = 10_000;
    /// Delay added by one auto-bump step.
    pub const AUTO_BUMP_STEP_MS: f64 = 10.0;

    /// Creates a buffer; the default profile is the conservative one.
    #[must_use]
    pub fn new(config: JitterBufferConfig, profile: EdgeJitterProfile, path: PathType) -> Self {
        let mut buffer = Self {
            config,
            profile,
            path,
            pending_path: None,
            rtt_ewma_ms: 0.0,
            jitter_ewma_ms: 0.0,
            has_samples: false,
            auto_bump_ms: 0.0,
            late_edge_ms: VecDeque::new(),
            current_delay: Duration::ZERO,
        };
        buffer.recompute();
        buffer
    }

    /// Base delays and adaptive mode.
    #[must_use]
    pub fn config(&self) -> JitterBufferConfig {
        self.config
    }

    /// Replaces the configuration and recomputes the delay.
    pub fn set_config(&mut self, config: JitterBufferConfig) {
        self.config = config;
        self.recompute();
    }

    /// The declared jitter profile.
    #[must_use]
    pub fn profile(&self) -> EdgeJitterProfile {
        self.profile
    }

    /// Replaces the profile and recomputes, so a mid-session flip to
    /// [`EdgeJitterProfile::DerpClassOnly`] widens the buffer immediately.
    pub fn set_profile(&mut self, profile: EdgeJitterProfile) {
        self.profile = profile;
        self.recompute();
    }

    /// The current path type.
    #[must_use]
    pub fn path(&self) -> PathType {
        self.path
    }

    /// Requests a path change. The new band is deferred to the next anchor reset so a
    /// band switch never stretches edges mid-word.
    pub fn set_path(&mut self, path: PathType) {
        if path == self.path {
            self.pending_path = None;
            return;
        }
        self.pending_path = Some(path);
    }

    /// True when a path change is waiting for the next anchor reset.
    #[must_use]
    pub fn has_pending_path_change(&self) -> bool {
        self.pending_path.is_some()
    }

    /// Commits a deferred path change; returns true when one was applied.
    pub fn apply_pending_path_change(&mut self) -> bool {
        match self.pending_path.take() {
            Some(path) => {
                self.path = path;
                self.recompute();
                true
            }
            None => false,
        }
    }

    /// Forces the path immediately, for session establishment or when no burst is running.
    pub fn set_path_immediate(&mut self, path: PathType) {
        self.path = path;
        self.pending_path = None;
        self.recompute();
    }

    /// Whether at least one RTT sample has been observed.
    #[must_use]
    pub fn has_samples(&self) -> bool {
        self.has_samples
    }

    /// Current RTT EWMA in milliseconds.
    #[must_use]
    pub fn rtt_ewma_ms(&self) -> f64 {
        self.rtt_ewma_ms
    }

    /// Current jitter EWMA in milliseconds.
    #[must_use]
    pub fn jitter_ewma_ms(&self) -> f64 {
        self.jitter_ewma_ms
    }

    /// Current auto-bump offset in milliseconds.
    #[must_use]
    pub fn auto_bump_ms(&self) -> f64 {
        self.auto_bump_ms
    }

    /// The delay currently applied to a newly anchored burst.
    #[must_use]
    pub fn current_delay(&self) -> Duration {
        self.current_delay
    }

    /// [`Self::current_delay`] in ticks of a clock running at `frequency` Hz.
    #[must_use]
    pub fn current_delay_in(&self, frequency: u64) -> u64 {
        if frequency == 0 {
            return 0;
        }
        (self.current_delay.as_nanos() as u64) * frequency / 1_000_000_000
    }

    /// Feeds one RTT measurement into the EWMAs and recomputes the delay.
    ///
    /// The jitter sample is the absolute deviation of this RTT from the RTT EWMA as it
    /// stood before this sample.
    pub fn observe_rtt(&mut self, rtt: Duration) {
        let sample_ms = rtt.as_secs_f64() * 1000.0;
        if !self.has_samples {
            // Seed rather than smooth from zero: smoothing from zero would understate the
            // delay for the first several samples, which is the unsafe direction.
            self.rtt_ewma_ms = sample_ms;
            self.jitter_ewma_ms = 0.0;
            self.has_samples = true;
        } else {
            let deviation_ms = (sample_ms - self.rtt_ewma_ms).abs();
            self.rtt_ewma_ms = Self::RTT_ALPHA * sample_ms + (1.0 - Self::RTT_ALPHA) * self.rtt_ewma_ms;
            self.jitter_ewma_ms =
                Self::JITTER_ALPHA * deviation_ms + (1.0 - Self::JITTER_ALPHA) * self.jitter_ewma_ms;
        }
        self.recompute();
    }

    /// Discards RTT and jitter history so the delay returns to the base for the band.
    pub fn reset_samples(&mut self) {
        self.rtt_ewma_ms = 0.0;
        self.jitter_ewma_ms = 0.0;
        self.has_samples = false;
        self.auto_bump_ms = 0.0;
        self.late_edge_ms.clear();
        self.recompute();
    }

    /// Reports a late edge arriving at `now_ms`; returns true when a bump was applied.
    pub fn report_late_edge(&mut self, now_ms: u64) -> bool {
        if !self.config.adaptive_mode {
            return false;
        }

        self.late_edge_ms.push_back(now_ms);
        let window_start = now_ms.saturating_sub(Self::LATE_STORM_WINDOW_MS);
        while let Some(&front) = self.late_edge_ms.front() {
            if front < window_start {
                self.late_edge_ms.pop_front();
            } else {
                break;
            }
        }

        if self.late_edge_ms.len() > Self::LATE_STORM_THRESHOLD {
            self.auto_bump_ms += Self::AUTO_BUMP_STEP_MS;

            // Clamp the bump so the total delay cannot exceed the band maximum.
            let max_ms = Self::max_delay_for(self.path, self.profile).as_secs_f64() * 1000.0;
            let base_ms = Self::base_delay_for(self.config, self.path, self.profile).as_secs_f64() * 1000.0;
            let max_bump = (max_ms - base_ms).max(0.0);
            if self.auto_bump_ms > max_bump {
                self.auto_bump_ms = max_bump;
            }

            self.late_edge_ms.clear();
            self.recompute();
            return true;
        }

        false
    }

    /// Whether the DERP-class band applies: on a relayed path, an unknown path, or
    /// whenever the profile is [`EdgeJitterProfile::DerpClassOnly`].
    #[must_use]
    pub fn uses_derp_band(path: PathType, profile: EdgeJitterProfile) -> bool {
        profile == EdgeJitterProfile::DerpClassOnly || path != PathType::Direct
    }

    /// Shortest delay permitted for the path under the profile.
    #[must_use]
    pub fn min_delay_for(path: PathType, profile: EdgeJitterProfile) -> Duration {
        if Self::uses_derp_band(path, profile) {
            Self::DERP_MIN
        } else {
            Self::DIRECT_MIN
        }
    }

    /// Longest delay permitted for the path under the profile.
    #[must_use]
    pub fn max_delay_for(path: PathType, profile: EdgeJitterProfile) -> Duration {
        if Self::uses_derp_band(path, profile) {
            Self::DERP_MAX
        } else {
            Self::DIRECT_MAX
        }
    }

    /// The configured base delay for the path under the profile, clamped into its band.
    #[must_use]
    pub fn base_delay_for(config: JitterBufferConfig, path: PathType, profile: EdgeJitterProfile) -> Duration {
        let configured = if Self::uses_derp_band(path, profile) { config.derp_delay } else { config.direct_delay };
        configured.clamp(
            Self::min_delay_for(path, profile),
            Self::max_delay_for(path, profile),
        )
    }

    /// The delay the adaptive formula yields for the given inputs.
    #[must_use]
    pub fn delay_for(
        config: JitterBufferConfig,
        path: PathType,
        profile: EdgeJitterProfile,
        has_samples: bool,
        jitter_ewma_ms: f64,
        auto_bump_ms: f64,
    ) -> Duration {
        let base = Self::base_delay_for(config, path, profile);
        let band_max = Self::max_delay_for(path, profile);
        let band_min = Self::min_delay_for(path, profile);

        let invalid_jitter = jitter_ewma_ms.is_nan() || jitter_ewma_ms < 0.0;
        if !config.adaptive_mode || !has_samples || invalid_jitter {
            if config.adaptive_mode && auto_bump_ms > 0.0 {
                let bumped_ms = base.as_secs_f64() * 1000.0 + auto_bump_ms;
                return Duration::from_secs_f64(bumped_ms / 1000.0).min(band_max).max(band_min);
            }
            return base;
        }

        let adaptive_ms = base.as_secs_f64() * 1000.0 + Self::JITTER_MULTIPLIER * jitter_ewma_ms + auto_bump_ms;
        // Guard the conversion: a runaway jitter EWMA must clamp, not overflow.
        let adaptive = if adaptive_ms >= band_max.as_secs_f64() * 1000.0 {
            band_max
        } else {
            Duration::from_secs_f64(adaptive_ms / 1000.0)
        };
        adaptive.clamp(band_min, band_max)
    }

    fn recompute(&mut self) {
        self.current_delay = Self::delay_for(
            self.config,
            self.path,
            self.profile,
            self.has_samples,
            self.jitter_ewma_ms,
            self.auto_bump_ms,
        );
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unknown_declaration_resolves_to_the_conservative_profile() {
        assert_eq!(EdgeJitterProfile::from_declaration(Some("PathAdaptive")), EdgeJitterProfile::PathAdaptive);
        assert_eq!(EdgeJitterProfile::from_declaration(Some("DerpClassOnly")), EdgeJitterProfile::DerpClassOnly);
        assert_eq!(EdgeJitterProfile::from_declaration(Some("tcp")), EdgeJitterProfile::DerpClassOnly);
        assert_eq!(EdgeJitterProfile::from_declaration(None), EdgeJitterProfile::DerpClassOnly);
    }

    #[test]
    fn unknown_path_uses_the_derp_band() {
        assert!(JitterBuffer::uses_derp_band(PathType::None, EdgeJitterProfile::PathAdaptive));
        assert!(JitterBuffer::uses_derp_band(PathType::Derp, EdgeJitterProfile::PathAdaptive));
        assert!(!JitterBuffer::uses_derp_band(PathType::Direct, EdgeJitterProfile::PathAdaptive));
        // DerpClassOnly forces the DERP band even on a direct path.
        assert!(JitterBuffer::uses_derp_band(PathType::Direct, EdgeJitterProfile::DerpClassOnly));
    }

    #[test]
    fn configured_base_delay_is_clamped_into_its_band() {
        let too_short = JitterBufferConfig {
            direct_delay: Duration::from_millis(1),
            derp_delay: Duration::from_millis(1),
            adaptive_mode: false,
        };
        assert_eq!(
            JitterBuffer::base_delay_for(too_short, PathType::Direct, EdgeJitterProfile::PathAdaptive),
            JitterBuffer::DIRECT_MIN
        );
        assert_eq!(
            JitterBuffer::base_delay_for(too_short, PathType::Derp, EdgeJitterProfile::PathAdaptive),
            JitterBuffer::DERP_MIN
        );

        let too_long = JitterBufferConfig {
            direct_delay: Duration::from_secs(9),
            derp_delay: Duration::from_secs(9),
            adaptive_mode: false,
        };
        assert_eq!(
            JitterBuffer::base_delay_for(too_long, PathType::Direct, EdgeJitterProfile::PathAdaptive),
            JitterBuffer::DIRECT_MAX
        );
        assert_eq!(
            JitterBuffer::base_delay_for(too_long, PathType::Derp, EdgeJitterProfile::PathAdaptive),
            JitterBuffer::DERP_MAX
        );
    }

    #[test]
    fn default_delays_are_sixty_and_two_hundred_ms() {
        let cfg = JitterBufferConfig::default();
        assert_eq!(
            JitterBuffer::base_delay_for(cfg, PathType::Direct, EdgeJitterProfile::PathAdaptive),
            Duration::from_millis(60)
        );
        assert_eq!(
            JitterBuffer::base_delay_for(cfg, PathType::Derp, EdgeJitterProfile::PathAdaptive),
            Duration::from_millis(200)
        );
    }

    #[test]
    fn adaptive_delay_adds_twice_the_jitter_ewma() {
        let cfg = JitterBufferConfig::default();
        let d = JitterBuffer::delay_for(cfg, PathType::Direct, EdgeJitterProfile::PathAdaptive, true, 20.0, 0.0);
        // 60 + 2*20 = 100ms, inside the direct band.
        assert_eq!(d, Duration::from_millis(100));
    }

    #[test]
    fn adaptive_delay_clamps_to_the_band_maximum() {
        let cfg = JitterBufferConfig::default();
        let d = JitterBuffer::delay_for(cfg, PathType::Direct, EdgeJitterProfile::PathAdaptive, true, 10_000.0, 0.0);
        assert_eq!(d, JitterBuffer::DIRECT_MAX, "runaway jitter must clamp, not overflow");
    }

    #[test]
    fn first_rtt_sample_seeds_the_ewma() {
        let mut jb = JitterBuffer::new(JitterBufferConfig::default(), EdgeJitterProfile::PathAdaptive, PathType::Direct);
        jb.observe_rtt(Duration::from_millis(40));
        assert!(jb.has_samples());
        assert!((jb.rtt_ewma_ms() - 40.0).abs() < 1e-9);
        assert_eq!(jb.jitter_ewma_ms(), 0.0);
        // Seeded, not smoothed from zero: the delay stays at the base.
        assert_eq!(jb.current_delay(), Duration::from_millis(60));
    }

    #[test]
    fn later_rtt_samples_move_the_delay_adaptively() {
        let mut jb = JitterBuffer::new(JitterBufferConfig::default(), EdgeJitterProfile::PathAdaptive, PathType::Direct);
        jb.observe_rtt(Duration::from_millis(40));
        jb.observe_rtt(Duration::from_millis(100));
        // jitter_ewma = 0.1 * |100 - 40| = 6ms; delay = 60 + 2*6 = 72ms.
        assert!((jb.jitter_ewma_ms() - 6.0).abs() < 1e-9);
        assert_eq!(jb.current_delay(), Duration::from_millis(72));
    }

    #[test]
    fn late_edge_storm_bumps_the_delay_one_step() {
        let mut jb = JitterBuffer::new(JitterBufferConfig::default(), EdgeJitterProfile::PathAdaptive, PathType::Direct);
        assert!(!jb.report_late_edge(1_000));
        assert!(!jb.report_late_edge(1_010));
        assert!(!jb.report_late_edge(1_020));
        // Fourth within the window crosses the threshold of three.
        assert!(jb.report_late_edge(1_030));
        assert!((jb.auto_bump_ms() - 10.0).abs() < 1e-9);
        assert_eq!(jb.current_delay(), Duration::from_millis(70));
    }

    #[test]
    fn late_edge_storm_requires_adaptive_mode() {
        let cfg = JitterBufferConfig { adaptive_mode: false, ..Default::default() };
        let mut jb = JitterBuffer::new(cfg, EdgeJitterProfile::PathAdaptive, PathType::Direct);
        for i in 0..10 {
            assert!(!jb.report_late_edge(1_000 + i));
        }
        assert_eq!(jb.auto_bump_ms(), 0.0);
    }

    #[test]
    fn path_change_is_deferred_until_the_next_anchor_reset() {
        let mut jb = JitterBuffer::new(JitterBufferConfig::default(), EdgeJitterProfile::PathAdaptive, PathType::Direct);
        jb.set_path(PathType::Derp);
        assert!(jb.has_pending_path_change());
        assert_eq!(jb.path(), PathType::Direct, "band must not switch mid-burst");
        assert_eq!(jb.current_delay(), Duration::from_millis(60));

        assert!(jb.apply_pending_path_change());
        assert_eq!(jb.path(), PathType::Derp);
        assert_eq!(jb.current_delay(), Duration::from_millis(200));
    }

    #[test]
    fn derp_class_only_ignores_a_direct_path() {
        let mut jb = JitterBuffer::new(
            JitterBufferConfig::default(),
            EdgeJitterProfile::DerpClassOnly,
            PathType::None,
        );
        jb.set_path_immediate(PathType::Direct);
        assert_eq!(jb.current_delay(), Duration::from_millis(200));
    }

    #[test]
    fn reset_samples_clears_history_and_returns_to_base() {
        let mut jb = JitterBuffer::new(JitterBufferConfig::default(), EdgeJitterProfile::PathAdaptive, PathType::Direct);
        jb.observe_rtt(Duration::from_millis(40));
        jb.observe_rtt(Duration::from_millis(120));
        jb.reset_samples();
        assert!(!jb.has_samples());
        assert_eq!(jb.current_delay(), Duration::from_millis(60));
        assert_eq!(jb.auto_bump_ms(), 0.0);
    }

    #[test]
    fn delay_in_ticks_matches_the_nanosecond_clock() {
        let jb = JitterBuffer::new(JitterBufferConfig::default(), EdgeJitterProfile::PathAdaptive, PathType::Direct);
        // 60ms at 1 GHz == 60_000_000 ticks.
        assert_eq!(jb.current_delay_in(1_000_000_000), 60_000_000);
    }
}
