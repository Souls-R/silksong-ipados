#!/usr/bin/env python3
"""Build a personal native iOS program from locally supplied game files."""
import argparse
from pathlib import Path
import sys


def main():
    if not __debug__:
        raise SystemExit('Run Python without -O: conversion assertions are required.')
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    resources = commands.add_parser('resources', help='Convert the complete macOS resource set')
    resources.add_argument('--game-data', type=Path, required=True)
    resources.add_argument('--output', type=Path, required=True)
    resources.add_argument('--jobs', type=int, choices=range(1, 9), default=2)
    resources.add_argument('--refresh', action='store_true', help='Reconvert instead of resuming local outputs')
    prepare = commands.add_parser('prepare', help='Generate a complete, portable native build directory')
    prepare.add_argument('--game-data', type=Path, required=True)
    prepare.add_argument('--unity-data', type=Path, required=True)
    prepare.add_argument('--input-system', type=Path, required=True, help='Official Input System 1.14.2 package directory')
    prepare.add_argument('--resources', type=Path, required=True)
    prepare.add_argument('--output', type=Path, required=True)
    prepare.add_argument('--bundle-id', required=True)
    prepare.add_argument('--app-name', default='Hollow Knight: Silksong', help='Build metadata label; home screen uses localized original titles')
    prepare.add_argument('--version', default='0.8.0')
    prepare.add_argument('--build-number', default='27')
    prepare.add_argument('--diagnostics', action='store_true', help='Default diagnostic settings on; users can change them in iPadOS Settings')
    prepare.add_argument('--metal-hud', action='store_true', help='Default native HUD on; users can change it in iPadOS Settings')
    native = commands.add_parser('native', help='Compile and package the prepared project on macOS')
    native.add_argument('--work', type=Path, required=True)
    native.add_argument('--jobs', type=int, choices=range(1, 33), default=6)
    archive = commands.add_parser('resource-zip', help='Package already converted resources for Files import')
    archive.add_argument('--resources', type=Path, required=True)
    archive.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == 'resources':
            from scripts.resources import prepare_resources
            prepare_resources(args)
        elif args.command == 'prepare':
            from scripts.prepare import prepare
            prepare(args)
        elif args.command == 'native':
            from scripts.native import build_native
            build_native(args)
        else:
            from scripts.resources import zip_resources
            zip_resources(args)
    except (OSError, ValueError, RuntimeError, AssertionError) as ex:
        print(f'Build stopped: {ex}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
