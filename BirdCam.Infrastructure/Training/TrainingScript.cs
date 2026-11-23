namespace BirdCam.Infrastructure.Training;

/// <summary>
/// Текст Python-скрипта обучения. Пишется в training/train.py и запускается
/// выбранным интерпретатором с ultralytics. Общается с приложением строками
/// BIRDCAM:{json} в stdout.
/// </summary>
public static class TrainingScript
{
    public const string FileName = "train.py";

    public const string Content = """
import argparse
import json
import os
import platform
import sys
import traceback
from pathlib import Path

os.environ.setdefault("MPLBACKEND", "Agg")
os.environ.setdefault("TQDM_DISABLE", "1")
os.environ.setdefault("YOLO_VERBOSE", "False")


def emit(kind, **payload):
    data = {"kind": kind}
    data.update(payload)
    print("BIRDCAM:" + json.dumps(data, ensure_ascii=False), flush=True)


def as_float(value):
    try:
        if value is None:
            return None
        if hasattr(value, "item"):
            value = value.item()
        number = float(value)
        if number != number or number in (float("inf"), float("-inf")):
            return None
        return number
    except Exception:
        return None


def mean_loss(trainer):
    items = getattr(trainer, "loss_items", None)
    if items is None:
        return None
    try:
        if isinstance(items, dict):
            values = [as_float(value) for value in items.values()]
            values = [value for value in values if value is not None]
            return sum(values) / len(values) if values else None
        if hasattr(items, "mean"):
            return as_float(items.mean())
        return as_float(items)
    except Exception:
        return None


def main():
    parser = argparse.ArgumentParser(description="BirdCam training")
    parser.add_argument("--data", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--run-name", required=True)
    parser.add_argument("--epochs", type=int, default=40)
    parser.add_argument("--imgsz", type=int, default=640)
    parser.add_argument("--batch", type=int, default=8)
    parser.add_argument("--device", default="cpu")
    parser.add_argument("--workers", type=int, default=2)
    parser.add_argument("--project", required=True)
    parser.add_argument("--opset", type=int, default=18)
    parser.add_argument("--no-export", action="store_true")
    args = parser.parse_args()

    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    data_path = Path(args.data).resolve()
    if not data_path.exists():
        emit("error", message=f"data.yaml not found: {data_path}")
        return 2

    try:
        from ultralytics import YOLO
        import torch
    except Exception as exc:
        emit("error", message="ultralytics/torch import failed: " + str(exc), traceback=traceback.format_exc())
        return 3

    emit("start", epochs=args.epochs, model=str(args.model), device=str(args.device), data=str(data_path))

    model = YOLO(str(args.model))

    def on_fit_epoch_end(trainer):
        metrics = {}
        for key, value in dict(getattr(trainer, "metrics", {}) or {}).items():
            number = as_float(value)
            if number is not None:
                metrics[str(key)] = number
        loss = mean_loss(trainer)
        total = int(getattr(trainer, "epochs", args.epochs))
        epoch = min(int(getattr(trainer, "epoch", 0)) + 1, total)
        payload = {
            "epoch": epoch,
            "epochs": total,
            "metrics": metrics,
        }
        if loss is not None:
            payload["loss"] = loss
        emit("metrics", **payload)

    def on_train_epoch_end(trainer):
        loss = mean_loss(trainer)
        total = int(getattr(trainer, "epochs", args.epochs))
        epoch = min(int(getattr(trainer, "epoch", 0)) + 1, total)
        payload = {
            "epoch": epoch,
            "epochs": total,
        }
        if loss is not None:
            payload["loss"] = loss
        emit("epoch", **payload)

    model.add_callback("on_fit_epoch_end", on_fit_epoch_end)
    model.add_callback("on_train_epoch_end", on_train_epoch_end)

    device = str(args.device)
    # AMP на Windows/ROCm пока нестабилен: для HIP-сборок обучаем в FP32.
    is_hip = bool(getattr(getattr(torch, "version", None), "hip", None))
    use_amp = device.lower() != "cpu" and not (platform.system() == "Windows" and is_hip)
    try:
        results = model.train(
            data=str(data_path),
            epochs=args.epochs,
            imgsz=args.imgsz,
            batch=args.batch,
            device=device,
            workers=args.workers,
            project=str(Path(args.project).resolve()),
            name=args.run_name,
            exist_ok=True,
            pretrained=True,
            seed=0,
            deterministic=True,
            plots=True,
            verbose=True,
            cache=False,
            save=True,
            val=True,
            amp=use_amp,
        )
    except Exception as exc:
        emit("error", message=str(exc), traceback=traceback.format_exc())
        return 4

    run_dir = Path(str(getattr(results, "save_dir", Path(args.project) / args.run_name))).resolve()
    best = run_dir / "weights" / "best.pt"
    if not best.exists():
        last = run_dir / "weights" / "last.pt"
        best = last if last.exists() else best

    if not best.exists():
        emit("error", message=f"best.pt not found in {run_dir}")
        return 5

    emit("trained", best=str(best), run=str(run_dir))

    if args.no_export:
        emit("done", best=str(best), onnx=None, run=str(run_dir))
        return 0

    emit("phase", phase="exporting", message="Экспорт best.pt в ONNX…")
    try:
        exported = YOLO(str(best)).export(
            format="onnx",
            opset=args.opset,
            imgsz=args.imgsz,
            simplify=True,
            dynamic=False,
        )
        onnx_path = Path(str(exported)).resolve()
        if not onnx_path.exists():
            candidate = best.with_suffix(".onnx")
            onnx_path = candidate if candidate.exists() else onnx_path
        if not onnx_path.exists():
            emit("error", message=f"ONNX export returned missing file: {exported}")
            return 6
        emit("exported", onnx=str(onnx_path), best=str(best))
    except Exception as exc:
        emit("error", message="ONNX export failed: " + str(exc), traceback=traceback.format_exc())
        return 7

    emit("done", best=str(best), onnx=str(onnx_path), run=str(run_dir))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        emit("error", message="KeyboardInterrupt")
        sys.exit(130)
    except Exception:
        emit("error", message="Unhandled error", traceback=traceback.format_exc())
        sys.exit(1)
""";
}
