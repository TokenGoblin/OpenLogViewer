//! A rectangular array, for the C#'s `T[,]`.

use std::ops::{Index, IndexMut};

/// `T[len0, len1]`, laid out as .NET lays it out: `[i, j]` is element
/// `i * len1 + j`. [`get_length`](Self::get_length) is the C#'s `GetLength`.
#[derive(Clone, Debug, PartialEq)]
pub struct Array2<T> {
    len0: usize,
    len1: usize,
    data: Vec<T>,
}

impl<T: Clone + Default> Array2<T> {
    /// `new T[len0, len1]`: every element the default.
    pub fn new(len0: usize, len1: usize) -> Self {
        Self {
            len0,
            len1,
            data: vec![T::default(); len0 * len1],
        }
    }
}

impl<T> Array2<T> {
    /// The length of dimension 0 or 1, as `GetLength(dimension)`.
    ///
    /// # Panics
    /// On a dimension other than 0 or 1, as the C# throws.
    pub fn get_length(&self, dimension: usize) -> usize {
        match dimension {
            0 => self.len0,
            1 => self.len1,
            _ => panic!("Array2 has two dimensions, not {}", dimension + 1),
        }
    }

    /// Every element, `[0, 0]`, `[0, 1]`, … in the C#'s order.
    pub fn as_slice(&self) -> &[T] {
        &self.data
    }

    /// `[i, j]`, or `None` out of range.
    pub fn get(&self, i: usize, j: usize) -> Option<&T> {
        (i < self.len0 && j < self.len1).then(|| &self.data[i * self.len1 + j])
    }

    /// The total element count, `Length` in the C#.
    pub fn len(&self) -> usize {
        self.data.len()
    }

    pub fn is_empty(&self) -> bool {
        self.data.is_empty()
    }
}

impl<T> Index<(usize, usize)> for Array2<T> {
    type Output = T;

    fn index(&self, (i, j): (usize, usize)) -> &T {
        assert!(
            i < self.len0 && j < self.len1,
            "[{i}, {j}] is outside {}x{}",
            self.len0,
            self.len1
        );
        &self.data[i * self.len1 + j]
    }
}

impl<T> IndexMut<(usize, usize)> for Array2<T> {
    fn index_mut(&mut self, (i, j): (usize, usize)) -> &mut T {
        assert!(
            i < self.len0 && j < self.len1,
            "[{i}, {j}] is outside {}x{}",
            self.len0,
            self.len1
        );
        &mut self.data[i * self.len1 + j]
    }
}
