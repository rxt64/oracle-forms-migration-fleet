#!/usr/bin/env python3

from __future__ import annotations

import copy
import importlib.util
import sys
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("update_auth_allowlist.py")
SPEC = importlib.util.spec_from_file_location("update_auth_allowlist", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


PRIMARY = "3f74daff-00b7-4606-bdb8-91aba713a54d"
OPERATOR = "dd84da40-177f-47b9-9c4d-4b657ee4de36"
DENIED = "6ec48e97-d7b0-468d-b605-efcbb5249a9a"
OTHER = "11111111-2222-4333-8444-555555555555"


def document(values):
    return {
        "id": "ignored",
        "properties": {
            "identityProviders": {
                "azureActiveDirectory": {
                    "registration": {"clientId": "unchanged"},
                    "validation": {
                        "defaultAuthorizationPolicy": {
                            "allowedPrincipals": {"identities": values}
                        }
                    },
                }
            }
        },
    }


class AuthAllowlistTests(unittest.TestCase):
    def test_build_preserves_valid_principals_and_removes_invalid_and_denied_values(self):
        source = document([OTHER.upper(), DENIED, f'"{OPERATOR}"', "not-a-guid"])
        result = MODULE.build(copy.deepcopy(source), {PRIMARY, OPERATOR}, {DENIED})
        values = result["properties"]["identityProviders"]["azureActiveDirectory"]["validation"][
            "defaultAuthorizationPolicy"
        ]["allowedPrincipals"]["identities"]

        self.assertEqual(sorted([OTHER, OPERATOR, PRIMARY]), values)
        self.assertEqual("unchanged", result["properties"]["identityProviders"]["azureActiveDirectory"]["registration"]["clientId"])

    def test_verify_rejects_quoted_missing_and_denied_values(self):
        with self.assertRaises(ValueError):
            MODULE.verify(document([f'"{OPERATOR}"', PRIMARY]), {OPERATOR, PRIMARY}, {DENIED})
        with self.assertRaises(ValueError):
            MODULE.verify(document([PRIMARY]), {OPERATOR, PRIMARY}, {DENIED})
        with self.assertRaises(ValueError):
            MODULE.verify(document([OPERATOR, PRIMARY, DENIED]), {OPERATOR, PRIMARY}, {DENIED})

    def test_verify_accepts_the_exact_boundary(self):
        MODULE.verify(document([OTHER, OPERATOR, PRIMARY]), {OPERATOR, PRIMARY}, {DENIED})


if __name__ == "__main__":
    unittest.main()