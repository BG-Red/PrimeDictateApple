#!/usr/bin/env python3
"""
Prepare QNN-friendly Moonshine artifacts for PrimeDictate.

This is a maintainer-only offline tool. End-user runtime inference remains pure
.NET/C# and uses ONNX Runtime directly inside PrimeDictate.

Expected calibration layout:

<calibration-dir>/
  preprocess/
    sample-000.npz
    ...
  encode/
    sample-000.npz
    ...
  uncached_decode/
    sample-000.npz
    ...
  cached_decode/
    sample-000.npz
    ...

Each .npz file must contain arrays keyed by the exact ONNX input names for that
stage. This keeps the pipeline reproducible and avoids baking stage-specific
capture logic into the quantizer itself.

The script writes generated QDQ models to:

<moonshine-model-dir>/qnn/
  preprocess.qdq.onnx
  encode.qdq.onnx
  uncached_decode.qdq.onnx
  cached_decode.qdq.onnx
  manifest.json

Notes:
- Best results typically come from quantizing float models. This scaffold will
  still attempt to preprocess and quantize the model files you point it at.
- Calibration data should be captured from representative 16 kHz mono dictation
  inputs and, for decoder stages, representative intermediate tensors collected
  from a known-good CPU reference run.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
from pathlib import Path
from typing import Dict, Iterator, List

import numpy as np
import onnxruntime as ort
from onnxruntime.quantization import CalibrationDataReader, QuantType, quantize
from onnxruntime.quantization.execution_providers.qnn import (
    get_qnn_qdq_config,
    qnn_preprocess_model,
)


STAGES = {
    "preprocess": "preprocess.onnx",
    "encode": "encode.int8.onnx",
    "uncached_decode": "uncached_decode.int8.onnx",
    "cached_decode": "cached_decode.int8.onnx",
}


class NpzCalibrationDataReader(CalibrationDataReader):
    def __init__(self, model_path: Path, samples_dir: Path) -> None:
        self._session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
        self._input_names = [item.name for item in self._session.get_inputs()]
        self._samples = self._load_samples(samples_dir)
        self._index = 0

    def get_next(self) -> Dict[str, np.ndarray] | None:
        if self._index >= len(self._samples):
            return None

        sample = self._samples[self._index]
        self._index += 1
        return sample

    def rewind(self) -> None:
        self._index = 0

    def _load_samples(self, samples_dir: Path) -> List[Dict[str, np.ndarray]]:
        if not samples_dir.is_dir():
            raise FileNotFoundError(f"Calibration directory not found: {samples_dir}")

        samples: List[Dict[str, np.ndarray]] = []
        for sample_path in sorted(samples_dir.glob("*.npz")):
            with np.load(sample_path, allow_pickle=False) as data:
                sample = {name: data[name] for name in self._input_names if name in data}

            missing = [name for name in self._input_names if name not in sample]
            if missing:
                raise ValueError(
                    f"Calibration sample '{sample_path}' is missing required inputs: {missing}"
                )

            samples.append(sample)

        if not samples:
            raise ValueError(f"No calibration .npz files found under {samples_dir}")

        return samples


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Quantize Moonshine stages for QNN EP")
    parser.add_argument(
        "--model-dir",
        required=True,
        help="Path to the Moonshine model directory containing preprocess/encode/decode/tokens files",
    )
    parser.add_argument(
        "--calibration-dir",
        required=True,
        help="Path to the root calibration directory with preprocess/encode/uncached_decode/cached_decode subfolders",
    )
    parser.add_argument(
        "--activation-type",
        choices=["uint8", "uint16"],
        default="uint16",
        help="Activation quantization type for generated QDQ models",
    )
    parser.add_argument(
        "--weight-type",
        choices=["uint8", "int8"],
        default="uint8",
        help="Weight quantization type for generated QDQ models",
    )
    return parser.parse_args()


def to_quant_type(value: str) -> QuantType:
    mapping = {
        "uint8": QuantType.QUInt8,
        "uint16": QuantType.QUInt16,
        "int8": QuantType.QInt8,
    }
    return mapping[value]


def ensure_stage_inputs(model_dir: Path, calibration_dir: Path) -> Iterator[tuple[str, Path, Path]]:
    for stage, file_name in STAGES.items():
        model_path = model_dir / file_name
        if not model_path.is_file():
            raise FileNotFoundError(f"Required Moonshine stage model not found: {model_path}")

        samples_dir = calibration_dir / stage
        yield stage, model_path, samples_dir


def main() -> None:
    args = parse_args()
    model_dir = Path(args.model_dir).expanduser().resolve()
    calibration_dir = Path(args.calibration_dir).expanduser().resolve()
    output_dir = model_dir / "qnn"
    output_dir.mkdir(parents=True, exist_ok=True)

    activation_type = to_quant_type(args.activation_type)
    weight_type = to_quant_type(args.weight_type)

    manifest = {
        "generated_utc": dt.datetime.utcnow().replace(microsecond=0).isoformat() + "Z",
        "onnxruntime_version": ort.__version__,
        "source_model_dir": str(model_dir),
        "calibration_dir": str(calibration_dir),
        "activation_type": args.activation_type,
        "weight_type": args.weight_type,
        "stages": {},
    }

    for stage, model_path, samples_dir in ensure_stage_inputs(model_dir, calibration_dir):
        print(f"Preparing {stage}: {model_path.name}")
        reader = NpzCalibrationDataReader(model_path, samples_dir)

        preprocessed_path = output_dir / f"{stage}.preprocessed.onnx"
        qdq_path = output_dir / f"{stage}.qdq.onnx"

        model_changed = qnn_preprocess_model(str(model_path), str(preprocessed_path))
        model_to_quantize = preprocessed_path if model_changed else model_path

        qnn_config = get_qnn_qdq_config(
            str(model_to_quantize),
            reader,
            activation_type=activation_type,
            weight_type=weight_type,
        )

        quantize(str(model_to_quantize), str(qdq_path), qnn_config)

        manifest["stages"][stage] = {
            "source_model": str(model_path),
            "preprocessed_model": str(preprocessed_path) if model_changed else None,
            "output_model": str(qdq_path),
            "calibration_samples": len(reader._samples),
        }

    manifest_path = output_dir / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"Wrote QNN Moonshine artifacts to {output_dir}")
    print(f"Manifest: {manifest_path}")


if __name__ == "__main__":
    main()