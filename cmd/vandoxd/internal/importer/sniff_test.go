package importer

import (
	"bytes"
	"testing"
)

// padded returns head followed by zero bytes up to n bytes.
func padded(head string, n int) []byte {
	return append([]byte(head), make([]byte, max(0, n-len(head)))...)
}

// tarHead returns a 512 byte head whose bytes 257 to 264 are magic.
func tarHead(magic string) []byte {
	h := bytes.Repeat([]byte("n"), 512)
	copy(h[257:], magic)
	return h
}

func TestSniffFormat(t *testing.T) {
	tests := []struct {
		name     string
		head     []byte
		want     format
		wantName string
	}{
		{"gzip", []byte{0x1f, 0x8b, 0x08, 0x00, 0x00}, formatGzip, ""},
		{"gzip with only the three signature bytes", []byte{0x1f, 0x8b, 0x08}, formatGzip, ""},
		{"gzip with another method byte is plain", []byte{0x1f, 0x8b, 0x09, 0x00}, formatPlain, ""},
		{"gzip signature cut short", []byte{0x1f, 0x8b}, formatPlain, ""},
		{"single 0x1f byte", []byte{0x1f}, formatPlain, ""},
		{"tar USTAR magic", tarHead("ustar\x0000"), formatTar, ""},
		{"tar GNU magic", tarHead("ustar  \x00"), formatTar, ""},
		{"tar magic at the wrong offset", append([]byte("ustar\x0000"), bytes.Repeat([]byte("n"), 504)...), formatPlain, ""},
		{"tar magic one byte early", func() []byte {
			h := bytes.Repeat([]byte("n"), 512)
			copy(h[256:], "ustar\x0000")
			return h
		}(), formatPlain, ""},
		{"tar head shorter than the magic", tarHead("ustar\x0000")[:261], formatPlain, ""},
		{"tar magic split by the end of the head", tarHead("ustar\x0000")[:262], formatPlain, ""},
		{"empty", []byte{}, formatEmpty, ""},
		{"nil", nil, formatEmpty, ""},
		{"bzip2", []byte("BZh91AY&SY"), formatUnsupported, "bzip2"},
		{"bzip2 signature only", []byte("BZh"), formatUnsupported, "bzip2"},
		{"xz", []byte{0xfd, '7', 'z', 'X', 'Z', 0x00, 0x00}, formatUnsupported, "xz"},
		{"zstd", []byte{0x28, 0xb5, 0x2f, 0xfd, 0x00}, formatUnsupported, "zstd"},
		{"lz4", []byte{0x04, 0x22, 0x4d, 0x18, 0x00}, formatUnsupported, "lz4"},
		{"zip", []byte{'P', 'K', 0x03, 0x04, 0x00}, formatUnsupported, "zip"},
		{"7z", []byte{'7', 'z', 0xbc, 0xaf, 0x27, 0x1c, 0x00}, formatUnsupported, "7z"},
		{"plain text", []byte("Oct  6 12:00:00 host kernel: Out of memory\n"), formatPlain, ""},
		{"text with a byte order mark", []byte("\xef\xbb\xbfOct  6 12:00:00\n"), formatPlain, ""},
		{"CRLF text", []byte("a\r\nb\r\n"), formatPlain, ""},
		{"NUL bytes", padded("", 100), formatPlain, ""},
		{"invalid UTF-8", []byte("a\xff\xfe\xfdb"), formatPlain, ""},
		{"text that merely starts like a signature", []byte("BZ not bzip2"), formatPlain, ""},
		{"text that contains a signature later", []byte("x PK\x03\x04"), formatPlain, ""},
		{"plain head of the full sniff length", bytes.Repeat([]byte("l"), 4096), formatPlain, ""},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got, name := sniffFormat(tc.head)

			if got != tc.want {
				t.Errorf("sniffFormat(%.20q) format = %d, want %d", tc.head, got, tc.want)
			}
			if tc.want == formatUnsupported && name != tc.wantName {
				t.Errorf("sniffFormat(%.20q) name = %q, want %q", tc.head, name, tc.wantName)
			}
		})
	}
}
