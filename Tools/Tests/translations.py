#!/usr/bin/env python3
"""
Checks BeaverBuddies' translations against English, the source: every file
parses, and has every English key once, in the same order, and nothing else.

    python3 Tools/Tests/translations.py
"""
import csv
import glob
import os
import sys

DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'BeaverBuddies', 'Localizations')


def keys(path):
    with open(path, encoding='utf-8', newline='') as f:
        return [row[0] for row in csv.reader(f) if row]


english = keys(os.path.join(DIR, 'enUS_BeaverBuddie.csv'))
failed = 0
for path in sorted(glob.glob(os.path.join(DIR, '*_BeaverBuddie.csv'))):
    lang = os.path.basename(path).split('_')[0]
    mine = keys(path)
    if mine == english:
        continue
    failed += 1
    twice = sorted({k for k in mine if mine.count(k) > 1})
    missing = [k for k in english if k not in mine]
    extra = [k for k in mine if k not in english]
    problems = (["twice: " + ", ".join(twice)] if twice else []) + (["missing: " + ", ".join(missing)] if missing else []) \
        + (["not in English: " + ", ".join(extra)] if extra else []) or ["keys out of order"]
    print(f"{lang}: " + "; ".join(problems))
print("every language ok" if failed == 0 else f"{failed} languages differ from English")
sys.exit(1 if failed else 0)
