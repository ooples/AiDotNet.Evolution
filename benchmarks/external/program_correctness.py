"""Versioned host-side contracts; never execute candidate code here.

mathematical-v1 permits equivalent optima. strict-upstream-v1 additionally requires
the pinned upstream validator (including its tie-breaking). Both require schema
checks and these host checks. Neither is a proof over all possible inputs.
"""
import base64
import copy
import hashlib
import heapq
import math

import numpy as np

CONTRACTS = ("strict-upstream-v1", "mathematical-v1")
RTOL, ATOL = 1e-6, 1e-8


def integer(value):
    return isinstance(value, (int, np.integer)) and not isinstance(value, (bool, np.bool_))


def number(value):
    return isinstance(value, (int, float, np.integer, np.floating)) and not isinstance(value, (bool, np.bool_)) and math.isfinite(value)


def field(value, key):
    if not isinstance(value, dict) or set(value) != {key}:
        raise ValueError("Wrong output fields")
    return value[key]


def array(value, shape):
    if not isinstance(value, (list, tuple, np.ndarray)):
        raise ValueError("Expected numeric array, not scalar")
    if not isinstance(value,np.ndarray):
        pending = [value]
        while pending:
            item = pending.pop()
            if isinstance(item,(list,tuple)):
                pending.extend(item)
            elif not number(item):
                raise ValueError("Non-numeric array element")
    a = np.asarray(value)
    if a.shape != shape or a.dtype.kind not in "iuf" or not np.all(np.isfinite(a)):
        raise ValueError("Wrong shape/type or nonfinite numeric output")
    return a.astype(float,copy=False)


def close(value, expected):
    actual = array(value, expected.shape)
    with np.errstate(over="ignore", invalid="ignore"):
        return bool(np.all(np.abs(actual-expected) <= ATOL + RTOL*np.abs(expected)))


class Components:
    def __init__(self, n):
        self.parents = list(range(n))
        self.count = n

    def root(self, x):
        while self.parents[x] != x:
            self.parents[x] = self.parents[self.parents[x]]
            x = self.parents[x]
        return x

    def join(self, a, b):
        a, b = self.root(a), self.root(b)
        if a == b:
            return False
        self.parents[a] = b
        self.count -= 1
        return True


def mathematical_check(task, problem):
    """Precompute trusted problem data once; return a bounded-output checker."""
    if task in ("base64_encoding", "sha256_hashing"):
        key = "encoded_data" if task == "base64_encoding" else "digest"
        answer = base64.b64encode(problem["plaintext"]) if task == "base64_encoding" else hashlib.sha256(problem["plaintext"]).digest()
        return lambda value: type(field(value, key)) is bytes and field(value, key) == answer
    if task == "count_connected_components":
        n = problem["num_nodes"]
        groups = Components(n)
        for a,b in problem["edges"]:
            groups.join(a,b)
        return lambda value: integer(field(value,"number_connected_components")) and field(value,"number_connected_components") == groups.count
    if task == "minimum_spanning_tree":
        n = problem["num_nodes"]
        # Match the pinned input graph's last-edge-wins semantics, but not its
        # arbitrary choice among equally optimal spanning forests.
        weights = {tuple(sorted((a,b))):float(w) for a,b,w in problem["edges"]}
        groups, optimum = Components(n), []
        for (a,b),w in sorted(weights.items(),key=lambda item:item[1]):
            if groups.join(a,b):
                optimum.append(w)
        optimum_weight = math.fsum(optimum)
        def mst(value):
            edges = field(value,"mst_edges")
            if not isinstance(edges,list) or len(edges) != len(optimum):
                return False
            chosen, values = Components(n), []
            for edge in edges:
                if not isinstance(edge,(list,tuple)) or len(edge) != 3:
                    return False
                a,b,w = edge
                if not integer(a) or not integer(b) or not 0 <= a < n or not 0 <= b < n or not number(w):
                    return False
                key = tuple(sorted((a,b)))
                if key not in weights or float(w) != weights[key] or not chosen.join(a,b):
                    return False
                values.append(float(w))
            return math.isclose(math.fsum(values),optimum_weight,rel_tol=RTOL,abs_tol=ATOL)
        return mst
    if task == "shortest_path_dijkstra":
        n = problem["shape"][0]
        directed = {}
        for a in range(n):
            for i in range(problem["indptr"][a],problem["indptr"][a+1]):
                b,w = int(problem["indices"][i]),float(problem["data"][i])
                if w < 0 or not math.isfinite(w):
                    raise ValueError("Dijkstra requires finite nonnegative weights")
                directed[a,b] = directed.get((a,b),0)+w
        graph = [dict() for _ in range(n)]
        for (a,b),w in directed.items():
            graph[a][b] = min(graph[a].get(b,math.inf),w)
            graph[b][a] = min(graph[b].get(a,math.inf),w)
        expected = []
        for start in range(n):
            distances, queue = [math.inf]*n, [(0.0,start)]
            distances[start] = 0.0
            while queue:
                distance,a = heapq.heappop(queue)
                if distance != distances[a]:
                    continue
                for b,w in graph[a].items():
                    if distance+w < distances[b]:
                        distances[b] = distance+w
                        heapq.heappush(queue,(distances[b],b))
            expected.append(distances)
        def paths(value):
            rows = field(value,"distance_matrix")
            if not isinstance(rows,list) or len(rows) != n:
                return False
            for row,answer in zip(rows,expected):
                if not isinstance(row,list) or len(row) != n:
                    return False
                for actual,wanted in zip(row,answer):
                    if math.isinf(wanted):
                        if actual is not None:
                            return False
                    elif not number(actual) or abs(actual-wanted) > ATOL+RTOL*abs(wanted):
                        return False
            return True
        return paths
    if task == "stable_matching":
        def prefs(raw):
            return [raw[i] for i in range(len(raw))]
        proposers,receivers = prefs(problem["proposer_prefs"]),prefs(problem["receiver_prefs"])
        n = len(proposers)
        ranks = [{p:i for i,p in enumerate(order)} for order in receivers]
        def stable(value):
            matching = field(value,"matching")
            if not isinstance(matching,list) or len(matching) != n or any(not integer(r) for r in matching) or set(matching) != set(range(n)):
                return False
            owners = {r:p for p,r in enumerate(matching)}
            for p,order in enumerate(proposers):
                for r in order:
                    if r == matching[p]:
                        break
                    if ranks[r][p] < ranks[r][owners[r]]:
                        return False
            return True
        return stable
    if task == "matrix_multiplication":
        a,b = np.asarray(problem["A"]),np.asarray(problem["B"])
        expected = np.einsum("ik,kj->ij",a,b,optimize=False)
        return lambda value: close(value,expected)
    if task == "outer_product":
        a,b = problem
        expected = np.asarray(a)[:,None]*np.asarray(b)[None,:]
        return lambda value: close(value,expected)
    if task == "convolve_1d":
        expected = np.convolve(*problem,mode="full")
        return lambda value: close(value,expected)
    if task == "correlate_1d":
        expected = [np.correlate(a,b,mode="full") for a,b in problem]
        return lambda value: isinstance(value,list) and len(value) == len(expected) and all(close(v,e) for v,e in zip(value,expected))
    if task == "unit_simplex_projection":
        y = np.asarray(problem["y"],dtype=float)
        if y.ndim != 1 or not len(y) or not np.all(np.isfinite(y)):
            raise ValueError("Simplex input requires a nonempty finite vector")
        def simplex(value):
            x = array(field(value,"solution"),y.shape)
            if np.any(x < 0) or not math.isclose(math.fsum(x),1,rel_tol=0,abs_tol=ATOL+RTOL):
                return False
            active = x > 0
            theta = float((y-x)[active][0])
            # KKT conditions certify the unique projection without copying the
            # reference's sorting/threshold algorithm.
            tolerance = ATOL+RTOL*np.maximum(np.abs(y),abs(theta))
            return bool(np.all(np.abs((y-x)[active]-theta) <= tolerance[active]) and
                        np.all(y[~active]-theta <= tolerance[~active]))
        return simplex
    raise ValueError("No correctness contract for task: "+task)


def validator(task, problem, upstream, *, contract):
    if contract not in CONTRACTS:
        raise ValueError("Unknown correctness contract")
    check = mathematical_check(task,copy.deepcopy(problem))
    def validate(value):
        try:
            if not check(value):
                return False
            return contract == "mathematical-v1" or bool(upstream.is_solution(copy.deepcopy(problem),copy.deepcopy(value)))
        except (ValueError,TypeError,KeyError,IndexError,OverflowError,RecursionError,AttributeError):
            return False
    return validate
