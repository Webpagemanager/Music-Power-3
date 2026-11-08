use memmap2::Mmap;
use std::fs::File;
use std::io;
use std::path::Path;

pub struct MmapReader {
    _file: File,
    mmap: Mmap,
}

impl MmapReader {
    pub fn open<P: AsRef<Path>>(path: P) -> io::Result<Self> {
        let file = File::open(path)?;
        // Safety: We only perform read-only operations on the mapped file slice.
        let mmap = unsafe { Mmap::map(&file)? };
        Ok(Self { _file: file, mmap })
    }

    #[inline]
    pub fn data(&self) -> &[u8] {
        &self.mmap
    }

    #[inline]
    pub fn len(&self) -> usize {
        self.mmap.len()
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.mmap.is_empty()
    }
}

