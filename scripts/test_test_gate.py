import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('gate', Path(__file__).with_name('check-tests.py'))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)

class GateTests(unittest.TestCase):
    def probe(self, counters, minimum=1):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'result.trx'
            path.write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary>'+counters+'</ResultSummary></TestRun>')
            return gate.check(path, minimum)
    def test_empty_discovery_is_not_success(self):
        with self.assertRaises(ValueError): self.probe('<Counters total="0" passed="0"/>')
    def test_failed_execution_is_not_success(self):
        with self.assertRaises(ValueError): self.probe('<Counters total="2" passed="1" failed="1"/>')
    def test_a_skipped_test_cannot_fill_the_required_floor(self):
        with self.assertRaises(ValueError): self.probe('<Counters total="2" passed="1" notExecuted="1"/>')
    def test_missing_counters_cannot_pass(self):
        with self.assertRaises(ValueError): self.probe('')
    def test_a_small_discovered_subset_cannot_replace_the_suite(self):
        with self.assertRaises(ValueError): self.probe('<Counters total="1" passed="1"/>', 392)
    def test_full_success_is_accepted(self):
        self.assertEqual(392, self.probe('<Counters total="392" passed="392" failed="0"/>', 392))

if __name__=='__main__':unittest.main()
