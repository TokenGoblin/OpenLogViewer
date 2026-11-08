//! Ported from `RunningCosts.cs`. Owned by package `w1-calc-b`.
//!
//! What it costs to run a car, and what it would cost to run a different one.
//! The comparison is where the traps are: a gallon is a volume, not an amount
//! of energy (E85 holds three-quarters of petrol's, diesel an eighth more); an
//! electric car is billed at the meter, not at the battery; and a saving per
//! mile is nearly invisible while a saving per year is not.

// The C# writes `!(x > 0)` so that NaN is refused; `x <= 0` would let it through.
#![allow(clippy::neg_cmp_op_on_partial_ord)]

/// What a vehicle runs on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FuelKind {
    Petrol,
    /// Not a fuel: a drivetrain that burns petrol, priced as petrol.
    Hybrid,
    E85,
    Diesel,
    /// Compressed natural gas, sold by the gasoline gallon equivalent.
    Cng,
    Electricity,
}

/// A fuel as you buy it: what it is sold by, and how much energy is in it.
///
/// Named apart from the tuning side's `Fuel`, which is about stoichiometry and
/// knock resistance; this is about what the pump charges and what you get.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct FuelSpec {
    pub kind: FuelKind,
    pub name: &'static str,
    /// What a unit of it is called when you buy it.
    pub unit: &'static str,
    /// How economy is quoted for it.
    pub efficiency_unit: &'static str,
    /// Energy in one unit, which is what makes fuels comparable at all.
    pub kwh_per_unit: f64,
    pub note: &'static str,
}

impl FuelSpec {
    /// The unit in the plural: gallons take an s, the abbreviations do not.
    pub fn unit_plural(&self) -> &'static str {
        if self.unit == "gallon" { "gallons" } else { self.unit }
    }

    /// Whether economy is quoted as miles per unit, which is all of them but one.
    pub fn per_unit(&self) -> bool {
        self.kind != FuelKind::Electricity
    }
}

/// Energy in a US gallon of petrol, and the definition everything else is
/// measured against (the EPA's figure; what MPGe and a CNG GGE are defined by).
pub const PETROL_KWH_PER_GALLON: f64 = 33.7;

/// The fuels this compares, and what is actually in them.
pub static FUELS: [FuelSpec; 6] = [
    FuelSpec {
        kind: FuelKind::Petrol,
        name: "Petrol",
        unit: "gallon",
        efficiency_unit: "mpg",
        kwh_per_unit: PETROL_KWH_PER_GALLON,
        note: "Regular unleaded.",
    },
    FuelSpec {
        kind: FuelKind::Hybrid,
        name: "Hybrid (petrol)",
        unit: "gallon",
        efficiency_unit: "mpg",
        kwh_per_unit: PETROL_KWH_PER_GALLON,
        note: "A petrol car that uses less of it \u{2014} same fuel, same price, better economy. A plug-in hybrid is a different thing and is not modelled here: what it costs depends on how much of your driving is done on the battery, which only you know.",
    },
    // 25.2 kWh: E85 is 51 to 83 per cent ethanol depending on the season and
    // the region, and the pump does not tell you which. This is the middle.
    FuelSpec {
        kind: FuelKind::E85,
        name: "E85",
        unit: "gallon",
        efficiency_unit: "mpg",
        kwh_per_unit: 25.2,
        note: "About three-quarters the energy of petrol, so a car on E85 does roughly 25% fewer miles to the gallon than the same car on petrol. If you have typed the same economy for both, one of them is wrong.",
    },
    FuelSpec {
        kind: FuelKind::Diesel,
        name: "Diesel",
        unit: "gallon",
        efficiency_unit: "mpg",
        kwh_per_unit: 37.7,
        note: "About an eighth more energy per gallon than petrol, and diesel engines are more efficient again \u{2014} which is why the economy looks so much better before you look at the price.",
    },
    FuelSpec {
        kind: FuelKind::Cng,
        name: "CNG",
        unit: "GGE",
        efficiency_unit: "mpg",
        kwh_per_unit: PETROL_KWH_PER_GALLON,
        note: "Sold by the gasoline gallon equivalent, which is defined as the same energy as a gallon of petrol \u{2014} so miles per GGE compares directly with mpg.",
    },
    FuelSpec {
        kind: FuelKind::Electricity,
        name: "Electric",
        unit: "kWh",
        efficiency_unit: "mi/kWh",
        kwh_per_unit: 1.0,
        note: "Charging is not free of losses: about a tenth of what the meter bills never reaches the battery, and you are billed for it either way. The cost here includes that; the MPGe does not, being what arrives at the wheels.",
    },
];

/// The spec for a kind of fuel.
pub fn fuel_for(kind: FuelKind) -> &'static FuelSpec {
    FUELS
        .iter()
        .find(|f| f.kind == kind)
        .expect("every FuelKind is in FUELS")
}

/// When the starting prices were taken (year, month, day), so their age is
/// visible rather than assumed.
pub const PRICES_CAPTURED_ON: (i32, u32, u32) = (2026, 8, 4);

/// Where each price came from, because a price with no source is a guess.
/// The date is written the way `d MMMM yyyy` prints in en-US.
pub fn price_source() -> String {
    "US national averages, 4 August 2026. Petrol, diesel and E85 from AAA. \
         CNG from the Alternative Fuels Data Center, whose last national figure is older \
         than the rest and is the one most worth replacing. Electricity is the US \
         residential average."
        .to_string()
}

/// National average pump price, per unit, or 0 for none.
pub fn price_for(kind: FuelKind) -> f64 {
    match kind {
        FuelKind::Petrol => 4.089,
        FuelKind::Hybrid => 4.089,
        FuelKind::E85 => 3.133,
        FuelKind::Diesel => 5.372,
        // October 2025, the most recent national average published; the least
        // current figure here.
        FuelKind::Cng => 2.96,
        FuelKind::Electricity => 0.1883,
    }
}

/// One vehicle, what it drinks and what that costs.
#[derive(Debug, Clone, PartialEq)]
pub struct VehicleCost {
    pub name: String,
    pub kind: FuelKind,
    /// Miles per unit for anything burnt, miles per kWh for anything plugged in.
    pub economy: f64,
    /// Per unit: per gallon, per GGE, per kWh.
    pub price: f64,
    /// Per cent lost between the meter and the battery, for an electric
    /// vehicle. Ten to fifteen per cent goes to heat and is billed to you.
    /// Ignored for anything that does not plug in. The C# default is 10.
    pub charging_loss: f64,
}

impl VehicleCost {
    pub fn new(name: &str, kind: FuelKind, economy: f64, price: f64, charging_loss: f64) -> Self {
        VehicleCost {
            name: name.to_string(),
            kind,
            economy,
            price,
            charging_loss,
        }
    }

    pub fn fuel(&self) -> &'static FuelSpec {
        fuel_for(self.kind)
    }

    /// Whether there is enough here to cost anything.
    pub fn is_usable(&self) -> bool {
        self.economy > 0.0 && self.price >= 0.0
    }

    /// Units bought per mile, which is where the charging loss lands: for an
    /// electric car the battery takes one thing and the meter charges for more.
    pub fn units_per_mile(&self) -> f64 {
        if !self.is_usable() {
            return f64::NAN;
        }

        let at_the_wheels = 1.0 / self.economy;

        if self.kind == FuelKind::Electricity {
            at_the_wheels / (1.0 - (self.charging_loss.clamp(0.0, 90.0) / 100.0))
        } else {
            at_the_wheels
        }
    }

    pub fn cost_per_mile(&self) -> f64 {
        self.units_per_mile() * self.price
    }

    pub fn units_per(&self, miles: f64) -> f64 {
        self.units_per_mile() * miles
    }

    pub fn cost_per(&self, miles: f64) -> f64 {
        self.cost_per_mile() * miles
    }

    /// Miles per gallon equivalent, miles per 33.7 kWh whatever it runs on: the
    /// only figure that compares different fuels honestly. Energy at the wheels,
    /// not at the meter, so charging loss is not counted twice.
    pub fn mpge(&self) -> f64 {
        if self.is_usable() {
            self.economy * PETROL_KWH_PER_GALLON / self.fuel().kwh_per_unit
        } else {
            f64::NAN
        }
    }

    /// Energy actually paid for, per mile, including what charging loses.
    pub fn kwh_per_mile(&self) -> f64 {
        if self.is_usable() {
            self.units_per_mile() * self.fuel().kwh_per_unit
        } else {
            f64::NAN
        }
    }
}

/// What a year's driving looks like when nobody has said.
pub const TYPICAL_ANNUAL_MILES: f64 = 12_000.0;

/// Weeks in a year, for turning an annual figure into a weekly one.
pub const WEEKS_PER_YEAR: f64 = 365.25 / 7.0;

/// Months, likewise: twelfths of a year rather than four-week blocks.
pub const MONTHS_PER_YEAR: f64 = 12.0;

fn cost(vehicle: &VehicleCost, annual_miles: f64) -> f64 {
    if annual_miles > 0.0 {
        vehicle.cost_per(annual_miles)
    } else {
        f64::NAN
    }
}

pub fn per_week(vehicle: &VehicleCost, annual_miles: f64) -> f64 {
    cost(vehicle, annual_miles) / WEEKS_PER_YEAR
}

pub fn per_month(vehicle: &VehicleCost, annual_miles: f64) -> f64 {
    cost(vehicle, annual_miles) / MONTHS_PER_YEAR
}

pub fn per_year(vehicle: &VehicleCost, annual_miles: f64) -> f64 {
    cost(vehicle, annual_miles)
}

/// The cheapest of several to run, or `None` when none of them can be costed.
/// Ties go to the first, as `MinBy` does.
pub fn cheapest(vehicles: &[VehicleCost]) -> Option<&VehicleCost> {
    let mut best: Option<&VehicleCost> = None;

    for v in vehicles.iter().filter(|v| v.is_usable()) {
        match best {
            Some(b) if !(v.cost_per_mile() < b.cost_per_mile()) => {}
            _ => best = Some(v),
        }
    }

    best
}

/// What one vehicle saves against another over a year, positive when the first
/// is cheaper.
pub fn annual_saving(cheaper: &VehicleCost, dearer: &VehicleCost, annual_miles: f64) -> f64 {
    if !cheaper.is_usable() || !dearer.is_usable() || annual_miles <= 0.0 {
        return f64::NAN;
    }

    dearer.cost_per(annual_miles) - cheaper.cost_per(annual_miles)
}

/// How long a price difference takes to pay back, in years. NaN where it never
/// pays back, which is not a failure: it is the answer.
pub fn years_to_pay_back(
    cheaper: &VehicleCost,
    dearer: &VehicleCost,
    annual_miles: f64,
    extra_purchase_price: f64,
) -> f64 {
    let saving = annual_saving(cheaper, dearer, annual_miles);

    if saving.is_nan() || saving <= 0.0 || extra_purchase_price <= 0.0 {
        return f64::NAN;
    }

    extra_purchase_price / saving
}

/// Whether an economy figure looks like it was copied from the petrol column:
/// E85 holds about three-quarters of petrol's energy, so typing the same mpg
/// for both makes the cheaper gallon look like a large saving.
pub fn looks_copied_from_petrol(vehicle: &VehicleCost, others: &[VehicleCost]) -> bool {
    if vehicle.kind != FuelKind::E85 || !vehicle.is_usable() {
        return false;
    }

    // Ten per cent of slack, because two different cars legitimately differ.
    others.iter().any(|o| {
        o.is_usable() && matches!(o.kind, FuelKind::Petrol | FuelKind::Hybrid) && vehicle.economy > o.economy * 0.9
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn close(expected: f64, actual: f64, digits: i32) {
        let tol = 0.5 * 10f64.powi(-digits);
        assert!(
            (expected - actual).abs() <= tol,
            "expected {expected}, got {actual} (to {digits} digits)"
        );
    }

    fn car_full(kind: FuelKind, economy: f64, price: Option<f64>, loss: f64) -> VehicleCost {
        VehicleCost::new(
            &format!("{kind:?}"),
            kind,
            economy,
            price.unwrap_or_else(|| price_for(kind)),
            loss,
        )
    }

    fn car(kind: FuelKind, economy: f64) -> VehicleCost {
        car_full(kind, economy, None, 10.0)
    }

    fn car_at(kind: FuelKind, economy: f64, price: f64) -> VehicleCost {
        car_full(kind, economy, Some(price), 10.0)
    }

    #[test]
    fn a_year_of_petrol_costs_what_the_gallons_cost() {
        // 12,000 miles at 30 mpg is 400 gallons; at $4.089 that is $1,635.60.
        let c = car_at(FuelKind::Petrol, 30.0, 4.089);

        close(400.0, c.units_per(12_000.0), 6);
        close(1635.60, per_year(&c, 12_000.0), 2);
        close(4.089 / 30.0, c.cost_per_mile(), 6);
    }

    #[test]
    fn the_weekly_and_monthly_figures_add_back_up_to_the_year() {
        let c = car_at(FuelKind::Petrol, 30.0, 4.089);

        let year = per_year(&c, 12_000.0);

        close(year, per_month(&c, 12_000.0) * 12.0, 6);
        close(year, per_week(&c, 12_000.0) * (365.25 / 7.0), 6);
    }

    #[test]
    fn e85_at_an_honest_economy_is_not_the_bargain_the_same_number_suggests() {
        let petrol = car_at(FuelKind::Petrol, 30.0, 4.089);

        // What somebody types when they copy the petrol figure across.
        let pretend = car_at(FuelKind::E85, 30.0, 3.133);

        // What the same car would actually do, energy for energy.
        let honest = 30.0 * (25.2 / 33.7);
        let real = car_at(FuelKind::E85, honest, 3.133);

        assert!(
            pretend.cost_per_mile() < petrol.cost_per_mile() * 0.8,
            "copied across, E85 looks more than 20% cheaper"
        );

        let ratio = real.cost_per_mile() / petrol.cost_per_mile();
        assert!((0.95..=1.10).contains(&ratio), "{ratio}");
    }

    #[test]
    fn an_e85_economy_copied_from_the_petrol_column_is_noticed() {
        let petrol = [car(FuelKind::Petrol, 30.0)];

        assert!(looks_copied_from_petrol(&car(FuelKind::E85, 30.0), &petrol));
        assert!(looks_copied_from_petrol(&car(FuelKind::E85, 28.0), &petrol));
        assert!(!looks_copied_from_petrol(&car(FuelKind::E85, 22.0), &petrol));
    }

    #[test]
    fn nothing_but_e85_is_flagged_for_it() {
        let petrol = [car(FuelKind::Petrol, 30.0)];

        assert!(!looks_copied_from_petrol(&car(FuelKind::Diesel, 40.0), &petrol));
        assert!(!looks_copied_from_petrol(&car(FuelKind::Electricity, 3.5), &petrol));
    }

    #[test]
    fn mpge_puts_different_fuels_on_one_scale() {
        close(30.0, car(FuelKind::Petrol, 30.0).mpge(), 6);
        close(30.0, car(FuelKind::E85, 30.0 * 25.2 / 33.7).mpge(), 6);

        // 30 mpg on diesel is 26.8 MPGe: a lower equivalent economy, not higher.
        close(30.0 * 33.7 / 37.7, car(FuelKind::Diesel, 30.0).mpge(), 6);
        assert!(car(FuelKind::Diesel, 30.0).mpge() < 30.0);

        assert!(car(FuelKind::E85, 30.0).mpge() > 30.0);

        // 33.7 kWh is one gallon equivalent by definition.
        close(33.7, car(FuelKind::Electricity, 1.0).mpge(), 6);
    }

    #[test]
    fn cng_compares_directly_because_its_unit_is_defined_that_way() {
        close(30.0, car(FuelKind::Cng, 30.0).mpge(), 6);
    }

    #[test]
    fn an_electric_car_is_costed_at_the_meter() {
        let ev = car_full(FuelKind::Electricity, 3.5, Some(0.1883), 10.0);

        close(1.0 / 3.5 / 0.9, ev.units_per_mile(), 9);
        close(1.0 / 3.5 / 0.9 * 0.1883, ev.cost_per_mile(), 9);

        let ignored = car_full(FuelKind::Electricity, 3.5, Some(0.1883), 0.0);

        assert!(ev.cost_per_mile() > ignored.cost_per_mile());
        close(1.0 / 0.9, ev.cost_per_mile() / ignored.cost_per_mile(), 6);
    }

    #[test]
    fn the_charging_loss_does_not_also_reduce_the_efficiency_figure() {
        close(
            car_full(FuelKind::Electricity, 3.5, None, 0.0).mpge(),
            car_full(FuelKind::Electricity, 3.5, None, 15.0).mpge(),
            6,
        );
    }

    #[test]
    fn nothing_that_does_not_plug_in_is_charged_a_charging_loss() {
        close(
            car_full(FuelKind::Petrol, 30.0, None, 0.0).cost_per_mile(),
            car_full(FuelKind::Petrol, 30.0, None, 50.0).cost_per_mile(),
            9,
        );
    }

    #[test]
    fn the_cheapest_to_run_is_the_one_with_the_lowest_cost_per_mile() {
        let all = [
            car(FuelKind::Petrol, 30.0),
            car(FuelKind::Diesel, 42.0),
            car(FuelKind::Electricity, 3.5),
        ];

        assert_eq!(Some(&all[2]), cheapest(&all));
    }

    #[test]
    fn an_unusable_vehicle_is_left_out_of_the_comparison_rather_than_winning_it() {
        let petrol = car(FuelKind::Petrol, 30.0);
        let blank = car(FuelKind::Diesel, 0.0);

        let both = [petrol.clone(), blank.clone()];
        assert_eq!(Some(&petrol), cheapest(&both));
        assert_eq!(None, cheapest(&[blank]));
        assert_eq!(None, cheapest(&[]));
    }

    #[test]
    fn a_saving_is_the_difference_over_the_year() {
        let petrol = car_at(FuelKind::Petrol, 30.0, 4.089);
        let ev = car_full(FuelKind::Electricity, 3.5, Some(0.1883), 10.0);

        let saving = annual_saving(&ev, &petrol, 12_000.0);

        close(petrol.cost_per(12_000.0) - ev.cost_per(12_000.0), saving, 6);
        assert!(saving > 0.0);
    }

    #[test]
    fn payback_is_the_extra_price_over_the_annual_saving() {
        let petrol = car_at(FuelKind::Petrol, 30.0, 4.089);
        let ev = car_at(FuelKind::Electricity, 3.5, 0.1883);

        let saving = annual_saving(&ev, &petrol, 12_000.0);
        let years = years_to_pay_back(&ev, &petrol, 12_000.0, 8_000.0);

        close(8_000.0 / saving, years, 6);
    }

    #[test]
    fn something_that_never_pays_back_says_so() {
        let thirsty = car_at(FuelKind::Petrol, 15.0, 4.089);
        let thirstier = car_at(FuelKind::Petrol, 12.0, 4.089);

        assert!(years_to_pay_back(&thirstier, &thirsty, 12_000.0, 5_000.0).is_nan());
        assert!(years_to_pay_back(&thirsty, &thirstier, 12_000.0, 0.0).is_nan());
    }

    #[test]
    fn every_fuel_has_a_price_a_source_and_a_date() {
        for f in &FUELS {
            assert!(price_for(f.kind) > 0.0, "{} has no starting price", f.name);
            assert!(!f.note.trim().is_empty(), "{} has no note", f.name);
            assert!(!f.unit.trim().is_empty());
        }

        assert!(price_source().contains("2026"));
        assert_eq!((2026, 8, 4), PRICES_CAPTURED_ON);
    }

    #[test]
    fn a_hybrid_is_priced_as_petrol_because_that_is_what_it_burns() {
        assert_eq!(price_for(FuelKind::Petrol), price_for(FuelKind::Hybrid));
        assert_eq!(
            fuel_for(FuelKind::Petrol).kwh_per_unit,
            fuel_for(FuelKind::Hybrid).kwh_per_unit
        );
    }

    #[test]
    fn the_plug_in_hybrid_gap_is_stated_rather_than_left_to_be_discovered() {
        assert!(fuel_for(FuelKind::Hybrid).note.to_lowercase().contains("plug-in"));
    }

    #[test]
    fn economy_is_quoted_in_the_unit_that_fuel_uses() {
        for (kind, expected) in [
            (FuelKind::Petrol, "mpg"),
            (FuelKind::Diesel, "mpg"),
            (FuelKind::Cng, "mpg"),
            (FuelKind::Electricity, "mi/kWh"),
        ] {
            assert_eq!(expected, fuel_for(kind).efficiency_unit);
        }
    }

    #[test]
    fn a_vehicle_with_no_economy_costs_nothing_rather_than_infinity() {
        let blank = car(FuelKind::Petrol, 0.0);

        assert!(!blank.is_usable());
        assert!(blank.cost_per_mile().is_nan());
        assert!(blank.mpge().is_nan());
        assert!(per_year(&blank, 12_000.0).is_nan());
    }

    #[test]
    fn no_mileage_means_no_annual_figure() {
        assert!(per_year(&car(FuelKind::Petrol, 30.0), 0.0).is_nan());
    }
}
