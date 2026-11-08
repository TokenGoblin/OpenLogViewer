//! Ported from `EngineFamilies.cs`. Owned by package `w1-calc-b`.
//!
//! Volumetric efficiency by the kind of engine it is. Every figure is a
//! convention with a wide spread around it; a particular engine's own measured
//! figure beats any of these, which is why the box stays editable and typing in
//! it moves the list to "measured or known".

/// A kind of engine, and how well it breathes.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct EngineFamily {
    /// What to call it in a list.
    pub name: &'static str,
    /// Peak volumetric efficiency, per cent.
    pub volumetric_efficiency: f64,
    /// Why it breathes the way it does.
    pub note: &'static str,
    /// Which of the cam profiles this description already assumes, as an index
    /// into that list. Zero for everything but the race entry. Naming it lets a
    /// page that also knows the cam avoid counting the cam twice.
    pub implied_cam_level: i32,
}

impl EngineFamily {
    /// True for the entry that stands for "whatever was typed in".
    pub fn is_custom(&self) -> bool {
        self.volumetric_efficiency <= 0.0
    }
}

impl std::fmt::Display for EngineFamily {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.name)
    }
}

/// The kinds worth offering, breathing worst first.
///
/// Peak volumetric efficiency, at the power peak, referenced to manifold
/// conditions: a boosted engine uses the same figure as the naturally aspirated
/// version of itself.
pub static ALL: [EngineFamily; 6] = [
    EngineFamily {
        name: "Older two-valve",
        volumetric_efficiency: 75.0,
        note: "small ports and a mild cam \u{2014} a 70s or 80s engine",
        implied_cam_level: 0,
    },
    EngineFamily {
        name: "Modern two-valve",
        volumetric_efficiency: 85.0,
        note: "a current pushrod V8 \u{2014} better than its valve count suggests",
        implied_cam_level: 0,
    },
    EngineFamily {
        name: "Four-valve, fixed cams",
        volumetric_efficiency: 92.0,
        note: "a twin-cam four-valve head without phasing",
        implied_cam_level: 0,
    },
    EngineFamily {
        name: "Four-valve with cam phasing",
        volumetric_efficiency: 97.0,
        note: "phasing broadens the peak as well as raising it",
        implied_cam_level: 0,
    },
    EngineFamily {
        name: "Race, tuned intake",
        volumetric_efficiency: 105.0,
        note: "throttle bodies and inlet lengths tuned to resonate",
        // The only entry whose description already has a big cam in it.
        implied_cam_level: 4,
    },
    EngineFamily {
        name: "Measured or known",
        volumetric_efficiency: 0.0,
        note: "your own figure, off a dyno or a log",
        implied_cam_level: 0,
    },
];

/// The family a volumetric efficiency corresponds to, or the custom entry.
///
/// Matched loosely: anything more than a couple of points from every entry is
/// somebody's own measurement and is labelled as such.
pub fn for_ve(ve_percent: f64) -> EngineFamily {
    if ve_percent.is_nan() || ve_percent <= 0.0 {
        return ALL[ALL.len() - 1];
    }

    for family in ALL {
        if !family.is_custom() && (family.volumetric_efficiency - ve_percent).abs() < 0.5 {
            return family;
        }
    }

    ALL[ALL.len() - 1]
}

/// Where that family sits in the list, for a combo box.
pub fn index_for(ve_percent: f64) -> usize {
    let family = for_ve(ve_percent);
    ALL.iter().position(|f| f.name == family.name).unwrap_or(ALL.len() - 1)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn they_are_ordered_from_worst_breathing_to_best() {
        let real: Vec<&EngineFamily> = ALL.iter().filter(|f| !f.is_custom()).collect();

        assert!(real.len() >= 5);

        for i in 1..real.len() {
            assert!(
                real[i].volumetric_efficiency > real[i - 1].volumetric_efficiency,
                "{} should breathe better than {}",
                real[i].name,
                real[i - 1].name
            );
        }
    }

    #[test]
    fn every_figure_is_one_a_real_engine_has() {
        for family in ALL.iter() {
            assert!(!family.name.trim().is_empty());
            assert!(!family.note.trim().is_empty());

            if family.is_custom() {
                continue;
            }

            assert!((70.0..=115.0).contains(&family.volumetric_efficiency));
        }
    }

    #[test]
    fn only_the_race_engine_is_allowed_past_a_hundred() {
        for family in ALL.iter() {
            if family.volumetric_efficiency > 100.0 {
                assert!(family.name.contains("Race"));
            }
        }
    }

    #[test]
    fn there_is_a_way_to_say_you_measured_it_yourself() {
        assert!(ALL[ALL.len() - 1].is_custom());
        assert_eq!(1, ALL.iter().filter(|f| f.is_custom()).count());
    }

    #[test]
    fn a_figure_from_the_list_is_recognised_as_that_kind_of_engine() {
        for (ve, expected) in [
            (75.0, "Older two-valve"),
            (85.0, "Modern two-valve"),
            (92.0, "Four-valve, fixed cams"),
            (97.0, "Four-valve with cam phasing"),
            (105.0, "Race, tuned intake"),
        ] {
            assert_eq!(expected, for_ve(ve).name);
        }
    }

    #[test]
    fn anything_else_is_treated_as_measured() {
        for ve in [88.0, 101.0, 63.0, 0.0, -5.0] {
            assert!(for_ve(ve).is_custom(), "{ve}");
        }
    }

    #[test]
    fn the_index_and_the_family_agree_with_each_other() {
        for family in ALL.iter() {
            if family.is_custom() {
                continue;
            }

            let at = index_for(family.volumetric_efficiency);

            assert_eq!(family.name, ALL[at].name);
        }

        assert_eq!(ALL.len() - 1, index_for(88.0));
    }
}
