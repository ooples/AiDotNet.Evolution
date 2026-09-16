"""Hand-written public contract fixtures, never sealed performance instances."""
import copy
import unittest
from unittest.mock import patch

import numpy as np

from program_correctness import ATOL, RTOL, validator
from program_controls import candidate_hash
from program_promotion import promote


class PermissiveUpstream:
    def is_solution(self, problem, value):
        return True


def fixtures():
    return {
        "base64_encoding": ({"plaintext":b"abc"},{"encoded_data":b"YWJj"}),
        "sha256_hashing": ({"plaintext":b"abc"},{"digest":bytes.fromhex("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")}),
        "count_connected_components": ({"num_nodes":5,"edges":[[0,1],[3,4]]},{"number_connected_components":3}),
        "minimum_spanning_tree": ({"num_nodes":3,"edges":[[0,1,1.],[0,2,1.],[1,2,1.]]},{"mst_edges":[[0,1,1.],[0,2,1.]]}),
        "shortest_path_dijkstra": ({"shape":[3,3],"data":[2.,2.],"indices":[1,0],"indptr":[0,1,2,2]},
                                   {"distance_matrix":[[0.,2.,None],[2.,0.,None],[None,None,0.]]}),
        "stable_matching": ({"proposer_prefs":[[0,1],[1,0]],"receiver_prefs":[[1,0],[0,1]]},{"matching":[0,1]}),
        "matrix_multiplication": ({"A":[[1.,2.,3.],[4.,5.,6.]],"B":[[1.,2.],[3.,4.],[5.,6.]]},[[22.,28.],[49.,64.]]),
        "outer_product": (([1.,2.],[3.,4.,5.]),[[3.,4.,5.],[6.,8.,10.]]),
        "convolve_1d": (([1.,2.],[3.,4.]),[3.,10.,8.]),
        "correlate_1d": ([([1.,2.],[3.,4.])],[[4.,11.,6.]]),
        "unit_simplex_projection": ({"y":[0.,0.]},{"solution":[.5,.5]}),
    }


class CorrectnessTests(unittest.TestCase):
    def test_all_eleven_tasks_have_positive_and_malformed_controls(self):
        from warm_panel import DEFINITIONS
        self.assertEqual(set(DEFINITIONS),set(fixtures()))
        for task,(problem,answer) in fixtures().items():
            with self.subTest(task=task):
                check = validator(task,problem,PermissiveUpstream(),contract="strict-upstream-v1")
                self.assertTrue(check(answer))
                for bad in (None,False,{},"wrong",0.5):
                    self.assertFalse(check(bad))

    def test_shape_broadcasting_and_nonfinite_outputs_never_pass(self):
        for task in ("outer_product","matrix_multiplication","convolve_1d","unit_simplex_projection"):
            problem,_ = fixtures()[task]
            check = validator(task,problem,PermissiveUpstream(),contract="mathematical-v1")
            for bad in (.5,[.5],[[.5,.5]], [float("nan"),.5],[float("inf"),.5],[True,.5]):
                value = {"solution":bad} if task == "unit_simplex_projection" else bad
                with self.subTest(task=task,bad=repr(bad)):
                    self.assertFalse(check(value))

    def test_numerical_tolerances_are_explicit_and_do_not_admit_large_errors(self):
        check = validator("outer_product",([1.],[1.]),PermissiveUpstream(),contract="mathematical-v1")
        self.assertTrue(check([[1+(ATOL+RTOL)/2]]))
        self.assertFalse(check([[1+2*(ATOL+RTOL)]]))
        self.assertFalse(check([[1e308]]))
        self.assertFalse(check([[True]]))

    def test_mst_alternative_is_distinguished_from_upstream_compatibility(self):
        problem,expected = fixtures()["minimum_spanning_tree"]
        class ExactReference:
            def is_solution(self,p,value):
                return value == expected
        alternative = {"mst_edges":[[0,1,1.],[1,2,1.]]}
        self.assertTrue(validator("minimum_spanning_tree",problem,ExactReference(),contract="mathematical-v1")(alternative))
        self.assertFalse(validator("minimum_spanning_tree",problem,ExactReference(),contract="strict-upstream-v1")(alternative))
        check = validator("minimum_spanning_tree",problem,PermissiveUpstream(),contract="mathematical-v1")
        for edges in ([[0,1,0.],[0,2,1.]],[[0,1,1.],[1,0,1.]],[[False,1,1.],[0,2,1.]],[[0,1,1.],[0,7,1.]]):
            self.assertFalse(check({"mst_edges":edges}))

    def test_disconnected_spanning_forest_and_empty_components(self):
        p = {"num_nodes":4,"edges":[[0,1,2.]]}
        self.assertTrue(validator("minimum_spanning_tree",p,PermissiveUpstream(),contract="mathematical-v1")({"mst_edges":[[0,1,2.]]}))
        check = validator("count_connected_components",{"num_nodes":0,"edges":[]},PermissiveUpstream(),contract="mathematical-v1")
        self.assertTrue(check({"number_connected_components":0}))
        self.assertFalse(check({"number_connected_components":False}))

    def test_multiple_stable_matchings_and_repeated_receiver_rejection(self):
        problem,_ = fixtures()["stable_matching"]
        check = validator("stable_matching",problem,PermissiveUpstream(),contract="mathematical-v1")
        self.assertTrue(check({"matching":[1,0]}))
        for value in ([0,0],[False,True],[0.,1.],[0,2]):
            self.assertFalse(check({"matching":value}))

    def test_unreachable_paths_require_none_not_infinity_or_fabricated_distance(self):
        problem,answer = fixtures()["shortest_path_dijkstra"]
        check = validator("shortest_path_dijkstra",problem,PermissiveUpstream(),contract="mathematical-v1")
        for bad in (0,1e30,float("inf"),False):
            changed = copy.deepcopy(answer)
            changed["distance_matrix"][0][2] = bad
            self.assertFalse(check(changed))

    def test_simplex_requires_optimality_not_just_sum_one(self):
        check = validator("unit_simplex_projection",{"y":[2.,0.]},PermissiveUpstream(),contract="mathematical-v1")
        self.assertTrue(check({"solution":[1.,0.]}))
        self.assertFalse(check({"solution":[.5,.5]}))
        self.assertFalse(check({"solution":[1.1,-.1]}))

    def test_search_answer_hardcoding_fails_a_different_public_fixture(self):
        old = fixtures()["base64_encoding"][1]
        check = validator("base64_encoding",{"plaintext":b"abcd"},PermissiveUpstream(),contract="mathematical-v1")
        self.assertFalse(check(old))
        self.assertTrue(check({"encoded_data":b"YWJjZA=="}))

    def test_public_randomized_matrix_and_simplex_controls(self):
        # These are public unit-test instances, unrelated to any registered study.
        rng = np.random.default_rng(731)
        for _ in range(20):
            a,b = rng.normal(size=(2,3)),rng.normal(size=(3,2))
            expected = [[sum(float(a[i,k])*float(b[k,j]) for k in range(3)) for j in range(2)] for i in range(2)]
            check = validator("matrix_multiplication",{"A":a,"B":b},PermissiveUpstream(),contract="mathematical-v1")
            self.assertTrue(check(expected))
            expected[0][0] += .5
            self.assertFalse(check(expected))
            y = rng.normal(size=2)
            first = min(1.,max(0.,(float(y[0])-float(y[1])+1)/2))
            projection = [first,1-first]
            check = validator("unit_simplex_projection",{"y":y},PermissiveUpstream(),contract="mathematical-v1")
            self.assertTrue(check({"solution":projection}))
            self.assertFalse(check({"solution":[projection[0]+.1,projection[1]]}))

    def test_warm_panel_rejects_scalar_despite_permissive_upstream(self):
        from warm_panel import prepare_task
        class Reference(PermissiveUpstream):
            def generate_problem(self,*a,**kw):
                return {"y":[0.,0.]}
            def solve(self,p):
                return {"solution":[.5,.5]}
        with patch("warm_panel.load_task",return_value=(Reference(),"fixture")),patch("algotune_worker.normalized_source",return_value=b"fixture"):
            task = prepare_task("fixture","unit_simplex_projection",partition="final",seeds=[1])
        self.assertFalse(task["validate"]([{"solution":.5}]))
        self.assertTrue(task["validate"]([{"solution":[.5,.5]}]))
        self.assertEqual("strict-upstream-v1",task["metadata"]["correctness_contract"])


def receipt(code,inputs,status="valid"):
    return dict(candidate_hash=candidate_hash(code),input_sha256=inputs,status=status,unknown_work=False,phase="confirmation")


class PromotionTests(unittest.TestCase):
    def setUp(self):
        self.diagnostics = {role:receipt(code,"diagnostic") for role,code in (("original","original"),("selected","selected"))}
        self.original = [receipt("original","audit-1"),receipt("original","audit-2")]
        self.selected = [receipt("selected","audit-1"),receipt("selected","audit-2")]

    def decide(self):
        return promote("original","selected",self.diagnostics,self.selected,self.original)

    def test_valid_selected_and_fully_audited_fallback(self):
        self.assertFalse(self.decide()["fallback"])
        self.selected[0]["status"] = "invalid"
        value = self.decide()
        self.assertTrue(value["fallback"])
        self.assertEqual(candidate_hash("original"),value["deployed_hash"])

    def test_failing_original_blocks_deployment_even_with_valid_candidate(self):
        self.original[0]["status"] = "invalid"
        with self.assertRaisesRegex(ValueError,"no deployment"):
            self.decide()

    def test_missing_replayed_wrong_source_wrong_phase_and_unknown_audits_refused(self):
        for key,value in (("candidate_hash","wrong"),("input_sha256","diagnostic"),("phase","search"),("unknown_work",True)):
            with self.subTest(key=key):
                original = copy.deepcopy(self.original)
                original[0][key] = value
                with self.assertRaises(ValueError):
                    promote("original","selected",self.diagnostics,self.selected,original)
        with self.assertRaises(ValueError):
            promote("original","selected",self.diagnostics,[],[])
        with self.assertRaises(ValueError):
            promote("original","selected",self.diagnostics,[self.selected[0]]*2,[self.original[0]]*2)


if __name__ == "__main__":
    unittest.main()
