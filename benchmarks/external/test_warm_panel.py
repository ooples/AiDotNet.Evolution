import unittest
import numpy as np

from warm_panel import decode, wire, prepare_task


class WireTests(unittest.TestCase):
    def test_roundtrip_preserves_arrays_tuples_bytes_and_nonstring_keys(self):
        value = {4:(b"abc",np.arange(6,dtype=np.float64).reshape(2,3)),"nested":[1,True,None]}
        result = decode(wire(value))
        self.assertEqual(b"abc",result[4][0])
        np.testing.assert_array_equal(value[4][1],result[4][1])
        self.assertEqual(value["nested"],result["nested"])

    def test_object_nonfinite_and_oversized_arrays_refused(self):
        for value in (np.array([object()]),np.array([float("nan")])):
            with self.assertRaises(ValueError):
                wire(value)
        for dtype,shape in (("O",[1]),("f8",[2**40]),("f8",[-1]),("f8",[True])):
            with self.assertRaises(ValueError):
                decode({"$array":{"dtype":dtype,"shape":shape,"data":""}})

    def test_duplicate_mapping_keys_and_unknown_tags_refused(self):
        for value in ({"$mapping":[[1,2],[1,3]]},{"$code":"print('bad')"}):
            with self.assertRaises(ValueError):
                decode(value)

    def test_final_family_cannot_be_opened_as_development(self):
        with self.assertRaises(ValueError):
            prepare_task("missing", "matrix_multiplication", partition="development", seeds=[1])
