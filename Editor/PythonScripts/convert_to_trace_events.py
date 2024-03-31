import json

def convert_to_trace_events(tree_data, pid=0, tid=0, ts=0):
    trace_events = []
    event_id = 1

    def generate_trace_event(name, cat, ts, dur, pid, tid):
        nonlocal event_id
        event = {
            'name': name,
            'cat': cat,
            'ts': ts,
            'dur': dur,
            'ph': 'X',
            'pid': pid,
            'tid': tid,
            'id': event_id,
        }
        event_id += 1
        return event

    def process_node(node, start_time, parent_name):
        dur = 0
        if 'name' in node:
            dur = node.get('duration', 0)
            event = generate_trace_event(node['name'], parent_name, start_time, dur, pid, tid)
            args = node.get('args', None)
            if args:
                event['args'] = args
            trace_events.append(event)

        if 'children' in node:
            for child in node['children']:
               start_time += process_node(child, start_time, node.get('name', parent_name))

        return dur

    process_node(tree_data, ts, 'ROOT')
    return trace_events

def convert_to_perfetto(tree_data):
    return {"traceEvents": convert_to_trace_events(tree_data)}



def convert_tree_to_speedscope(tree_data):
    names = [] # [{"name": "a"}, {"name": "b"}, {"name": "c"}, {"name": "d"}]
    samples = [] #[[0, 1, 2], [0, 1, 2], [0, 1, 3], [0, 1, 2], [0, 1]]
    weights = [] # [1, 1, 4, 3, 5]
    end_value = 0
    path_ids = []
    def process_node(node):
        value = 0
        rvalue = 0
        if 'name' in node:
            rvalue = value = node.get('duration', 0)
            if node['name'] not in names:
                names.append(node['name'])
            path_ids.append(names.index(node['name']))

        if 'children' in node:
            for child in node['children']:
               value -= process_node(child)

        if 'name' in node and value > 0:
            samples.append(path_ids.copy())
            weights.append(value)
            path_ids.pop()

        return rvalue

    end_value = process_node(tree_data)
    return{
        "version": "0.1.2",
        "$schema": "https://www.speedscope.app/file-format-schema.json",
        "profiles": [
            {
                "type": "sampled",
                "name": "simple.speedscope.json",
                "unit": "seconds",
                "startValue": 0,
                "endValue": end_value,
                "samples": samples,
                "weights": weights
            }
        ],
        "shared": {
            "frames": [{'name': name} for name in names]
        }
    }

# Example input tree data
tree_data = {
    'name': 'A',
    'duration': 10,
    'children': [
        {
            'name': 'B',
            'duration': 5
        },
        {
            'name': 'C',
            'duration': 3,
            'children': [
                {
                    'name': 'D',
                    'duration': 2
                }
            ]
        },
        {
            'name': 'Other',
            'duration': 2
        }
    ],
    "args": {
        'key1': 'value1',
        'key2': 'value2'
    }
}

trace_events = convert_to_trace_events(tree_data)
with open('trace.json', 'w') as f:
    json.dump(trace_events, f, indent=2)

print("trace 数据已生成到 trace.json 文件中")

perfetto_data = convert_to_perfetto(tree_data)
with open("perfetto.json", "w") as f:
    json.dump(perfetto_data, f, indent=2)

print("Perfetto 数据已生成到 perfetto.json 文件中")

speedscope_data = convert_tree_to_speedscope(tree_data)
with open("speedscope.json", "w") as f:
    json.dump(speedscope_data, f, indent=2)

print("Perfetto 数据已生成到 speedscope.json 文件中")



