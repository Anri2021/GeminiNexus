"""Bounded-memory concurrent-run smoke benchmark against the deterministic provider."""
import concurrent.futures, pathlib, statistics, time
from integration import Contracts

def rss_bytes(pid):
    for line in pathlib.Path(f'/proc/{pid}/status').read_text().splitlines():
        if line.startswith('VmRSS:'):return int(line.split()[1])*1024
    return 0

Contracts.setUpClass();case=Contracts(methodName='runTest')
try:
    baseline=rss_bytes(Contracts.process.pid);latencies=[]
    def execute(index):
        started=time.perf_counter();conversation=case.conversation();status,run=case.submit(conversation,f'load-{index}');assert status==202
        completed=case.wait(run);assert completed['status']=='completed';return time.perf_counter()-started
    with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
        for latency in pool.map(execute,range(40)):latencies.append(latency)
    final=rss_bytes(Contracts.process.pid);growth=max(0,final-baseline);p95=statistics.quantiles(latencies,n=20)[18]
    assert growth<160*1024*1024,(baseline,final,growth)
    assert p95<20,(p95,latencies)
    print(f'load acceptance passed: 40 runs, p95={p95:.3f}s, rss_growth={growth/1048576:.1f}MiB')
finally:Contracts.tearDownClass()
