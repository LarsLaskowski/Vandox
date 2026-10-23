package journal

// Decompressor decodes the zstd frame of a compressed item.
type Decompressor interface {
	// Decompress appends the decoded frame src to dst[:0], at most limit bytes; an error makes the item undecoded.
	Decompress(dst, src []byte, limit int) ([]byte, error)
}

// ReadOptions configures NextWith.
type ReadOptions struct {
	MaxPayload         int          // bytes of a wanted payload kept, after decompression
	Decompress         Decompressor // nil: compressed items stay undecoded
	MaxCompressed      int          // compressed items decoded per entry; one more leaves the entry undecoded
	MaxCompressedBytes int          // largest compressed object read; a larger one is undecoded without being read
}

// SeekAll positions a cursor on the file's main entry array at the first entry after the sequence number after.
func (f *File) SeekAll(after uint64) (Cursor, error) {
	return Cursor{}, errNotImplemented
}

// NextWith reads the next entry of the main entry array into e like Next, with the limits and the decompression of opt. It reports
// whether an entry was read.
func (f *File) NextWith(c *Cursor, want []string, e *Entry, opt *ReadOptions) (bool, error) {
	return false, errNotImplemented
}
