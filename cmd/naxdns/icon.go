package main

import (
	"bytes"
	"encoding/binary"
	"image"
	"image/color"
	"image/png"
	"math"
)

var (
	colorOn   = color.RGBA{0x34, 0xd3, 0x99, 0xff}
	colorWarn = color.RGBA{0xfb, 0xbf, 0x24, 0xff}
	colorOff  = color.RGBA{0x8b, 0x93, 0xa7, 0xff}
)

const iconSize = 64

// iconPNG draws the Nax-DNS mark — a ring with a solid core — in the given colour.
func iconPNG(c color.RGBA) []byte {
	const size = iconSize
	img := image.NewRGBA(image.Rect(0, 0, size, size))
	centre := float64(size-1) / 2
	for y := 0; y < size; y++ {
		for x := 0; x < size; x++ {
			d := math.Hypot(float64(x)-centre, float64(y)-centre)
			// coverage of the ring (22..29) and the core (0..13), antialiased
			a := math.Max(clamp(29.5-d)*clamp(d-21.5), clamp(13.5-d))
			img.SetRGBA(x, y, color.RGBA{
				uint8(float64(c.R) * a), uint8(float64(c.G) * a), uint8(float64(c.B) * a), uint8(255 * a),
			})
		}
	}
	var pngData bytes.Buffer
	png.Encode(&pngData, img)
	return pngData.Bytes()
}

// icon wraps iconPNG as a PNG-compressed .ico for the tray.
func icon(c color.RGBA) []byte {
	const size = iconSize
	pngData := bytes.NewBuffer(iconPNG(c))

	var ico bytes.Buffer
	binary.Write(&ico, binary.LittleEndian, []uint16{0, 1, 1}) // reserved, type icon, one image
	ico.Write([]byte{size, size, 0, 0})                        // width, height, palette, reserved
	binary.Write(&ico, binary.LittleEndian, []uint16{1, 32})   // planes, bits per pixel
	binary.Write(&ico, binary.LittleEndian, []uint32{uint32(pngData.Len()), 22})
	ico.Write(pngData.Bytes())
	return ico.Bytes()
}

func clamp(v float64) float64 { return math.Max(0, math.Min(1, v)) }
