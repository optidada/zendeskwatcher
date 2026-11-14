"""
Packs a Model2Vec static embedding model (e.g. minishlab/potion-base-8M, MIT) into the small file the app reads
(Core/Assets/embeddings.bin, see Core/Tracking/Embeddings.cs). Standard library only.

    python -I Tools/pack-model.py <folder with model.safetensors + tokenizer.json> Core/Assets/embeddings.bin

Format (little endian): "ZWE1", uint32 vocab size, uint32 dimensions; per token (in id order) uint16 byte length +
UTF-8; float32 scale per token; then int8 values, token by token (value = int8 * scale).
"""
import array
import json
import struct
import sys

src, out = sys.argv[1], sys.argv[2]

with open(f"{src}/tokenizer.json", encoding="utf-8") as f:
    tokenizer = json.load(f)
assert tokenizer["model"]["type"] == "WordPiece", tokenizer["model"]["type"]
vocab = tokenizer["model"]["vocab"]
tokens = [None] * len(vocab)
for token, index in vocab.items():
    tokens[index] = token
assert all(t is not None for t in tokens)

with open(f"{src}/model.safetensors", "rb") as f:
    header_len = struct.unpack("<Q", f.read(8))[0]
    header = json.loads(f.read(header_len))
    info = header["embeddings"]
    assert info["dtype"] == "F32", info
    rows, dim = info["shape"]
    assert rows == len(tokens), (rows, len(tokens))
    start, end = info["data_offsets"]
    f.seek(8 + header_len + start)
    values = array.array("f")
    values.frombytes(f.read(end - start))
    if sys.byteorder != "little":
        values.byteswap()

scales = array.array("f")
data = bytearray(rows * dim)
for r in range(rows):
    row = values[r * dim:(r + 1) * dim]
    scale = max(abs(v) for v in row) / 127 or 1.0
    scales.append(scale)
    for c, v in enumerate(row):
        data[r * dim + c] = max(-127, min(127, round(v / scale))) & 0xFF

with open(out, "wb") as f:
    f.write(b"ZWE1")
    f.write(struct.pack("<II", rows, dim))
    for token in tokens:
        raw = token.encode("utf-8")
        f.write(struct.pack("<H", len(raw)))
        f.write(raw)
    f.write(scales.tobytes())
    f.write(bytes(data))
print(f"{rows} tokens x {dim} -> {out}")
