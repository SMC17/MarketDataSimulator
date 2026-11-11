#!/usr/bin/env python3
"""Refuse a successful test command that discovered no tests or recorded failures."""
import argparse
from pathlib import Path
import xml.etree.ElementTree as ET


def check(path, minimum=1):
    root = ET.parse(path).getroot()
    counters = next((node for node in root.iter() if node.tag.rsplit('}', 1)[-1] == 'Counters'), None)
    if counters is None:
        raise ValueError('TRX contains no counters')
    total = int(counters.get('total', '0'))
    passed = int(counters.get('passed', '0'))
    failed = int(counters.get('failed', '0')) + int(counters.get('error', '0'))
    if total < minimum or passed != total or failed:
        raise ValueError(f'expected at least {minimum} passing tests and zero failures/skips; total={total}, passed={passed}, failures={failed}')
    return total


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trx', type=Path)
    parser.add_argument('--minimum', type=int, default=1)
    args = parser.parse_args()
    print(f'{check(args.trx, args.minimum)} tests executed and passed')
