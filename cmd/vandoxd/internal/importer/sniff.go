package importer

import "bytes"

// format is the container format of a file's content.
type format int

const (
	formatPlain format = iota
	formatEmpty
	formatGzip
	formatTar
	formatUnsupported
)

// tarMagicOffset is the offset of the magic of a USTAR or GNU tar header.
const tarMagicOffset = 257

// tarMagics are the 8 bytes at tarMagicOffset of a USTAR (with version "00") and of a GNU tar header.
var tarMagics = [][]byte{[]byte("ustar\x0000"), []byte("ustar  \x00")}

// unsupportedFormats are the signatures of compressions and archives that are listed, not imported.
var unsupportedFormats = []struct {
	magic []byte
	name  string
}{
	{[]byte("BZh"), "bzip2"},
	{[]byte{0xfd, '7', 'z', 'X', 'Z', 0x00}, "xz"},
	{[]byte{0x28, 0xb5, 0x2f, 0xfd}, "zstd"},
	{[]byte{0x04, 0x22, 0x4d, 0x18}, "lz4"},
	{[]byte{'P', 'K', 0x03, 0x04}, "zip"},
	{[]byte{'7', 'z', 0xbc, 0xaf, 0x27, 0x1c}, "7z"},
}

// sniffFormat classifies head; for formatUnsupported it also returns the format's name.
func sniffFormat(head []byte) (format, string) {
	if len(head) == 0 {
		return formatEmpty, ""
	}
	if bytes.HasPrefix(head, []byte{0x1f, 0x8b, 0x08}) {
		return formatGzip, ""
	}
	for _, u := range unsupportedFormats {
		if bytes.HasPrefix(head, u.magic) {
			return formatUnsupported, u.name
		}
	}
	if isTar(head) {
		return formatTar, ""
	}
	return formatPlain, ""
}

// isTar reports whether head starts with a USTAR or GNU tar header.
func isTar(head []byte) bool {
	for _, magic := range tarMagics {
		if len(head) >= tarMagicOffset+len(magic) && bytes.Equal(head[tarMagicOffset:tarMagicOffset+len(magic)], magic) {
			return true
		}
	}
	return false
}
