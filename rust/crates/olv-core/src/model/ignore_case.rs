//! `StringComparer.OrdinalIgnoreCase`, and the dictionary the C# builds with it.

use std::collections::HashMap;

/// The simple uppercase mapping .NET's ordinal ignore-case comparison uses: a
/// character whose uppercase is more than one character (`ß`) compares as itself.
fn fold_char(c: char) -> char {
    let mut upper = c.to_uppercase();
    match (upper.next(), upper.next()) {
        (Some(u), None) => u,
        _ => c,
    }
}

fn fold(s: &str) -> String {
    s.chars().map(fold_char).collect()
}

/// `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`.
pub fn eq_ignore_case(a: &str, b: &str) -> bool {
    a.chars().map(fold_char).eq(b.chars().map(fold_char))
}

/// `Dictionary<string, V>(StringComparer.OrdinalIgnoreCase)`.
///
/// Keeps what a port depends on: lookups ignore case, enumeration is in insertion
/// order, and overwriting a key through [`insert`](Self::insert) (the C#'s
/// indexer) keeps the casing it was first added with. The one difference: after
/// a [`remove`](Self::remove), .NET reuses the freed slot for the next insert,
/// which this does not.
#[derive(Clone, Debug)]
pub struct IgnoreCaseMap<V> {
    entries: Vec<(String, V)>,
    index: HashMap<String, usize>,
}

impl<V> Default for IgnoreCaseMap<V> {
    fn default() -> Self {
        Self {
            entries: Vec::new(),
            index: HashMap::new(),
        }
    }
}

impl<V> IgnoreCaseMap<V> {
    pub fn new() -> Self {
        Self::default()
    }

    /// `map[key] = value`: replaces the value under an existing key (whose casing
    /// stays as first added), or adds the pair.
    pub fn insert(&mut self, key: &str, value: V) {
        let folded = fold(key);
        match self.index.get(&folded) {
            Some(&i) => self.entries[i].1 = value,
            None => {
                self.index.insert(folded, self.entries.len());
                self.entries.push((key.to_string(), value));
            }
        }
    }

    /// `TryAdd`: adds the pair only when the key is new, and says whether it did.
    pub fn try_add(&mut self, key: &str, value: V) -> bool {
        if self.contains_key(key) {
            return false;
        }
        self.insert(key, value);
        true
    }

    pub fn get(&self, key: &str) -> Option<&V> {
        self.index.get(&fold(key)).map(|&i| &self.entries[i].1)
    }

    pub fn get_mut(&mut self, key: &str) -> Option<&mut V> {
        self.index.get(&fold(key)).map(|&i| &mut self.entries[i].1)
    }

    /// The key as it was first added, and its value.
    pub fn get_key_value(&self, key: &str) -> Option<(&str, &V)> {
        self.index
            .get(&fold(key))
            .map(|&i| (self.entries[i].0.as_str(), &self.entries[i].1))
    }

    pub fn contains_key(&self, key: &str) -> bool {
        self.index.contains_key(&fold(key))
    }

    pub fn remove(&mut self, key: &str) -> Option<V> {
        let i = self.index.remove(&fold(key))?;
        let (_, value) = self.entries.remove(i);
        for slot in self.index.values_mut() {
            if *slot > i {
                *slot -= 1;
            }
        }
        Some(value)
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// The pairs in insertion order.
    pub fn iter(&self) -> impl Iterator<Item = (&str, &V)> {
        self.entries.iter().map(|(k, v)| (k.as_str(), v))
    }

    pub fn keys(&self) -> impl Iterator<Item = &str> {
        self.entries.iter().map(|(k, _)| k.as_str())
    }

    pub fn values(&self) -> impl Iterator<Item = &V> {
        self.entries.iter().map(|(_, v)| v)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn overwriting_keeps_the_first_casing_and_the_order() {
        let mut map = IgnoreCaseMap::new();
        map.insert("veTable1", 1);
        map.insert("rpm", 2);
        map.insert("VETABLE1", 3);
        assert_eq!(map.iter().collect::<Vec<_>>(), [("veTable1", &3), ("rpm", &2)]);
        assert!(!map.try_add("RPM", 9));
        assert_eq!(map.remove("Rpm"), Some(2));
        assert_eq!(map.get("vetable1"), Some(&3));
        assert!(eq_ignore_case("Straße", "STRAßE"));
    }
}
