// This file is part of the Prowl Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

using Prowl.Echo;
using Prowl.Editor.Docking;
using Prowl.Editor.Widgets;
using Prowl.PaperUI;
using Prowl.Runtime;
using Prowl.Vector;

namespace Prowl.Editor.Panels;

[EditorWindow("Debug/Serialization Playground")]
public class SerializationTesterPanel : DockPanel
{
    public override string Title => "Serialization Tester";

    public class TestSerializeClass
    {
        public int IntValue;
        public string StringValue;
        public float FloatValue;

        public Float3 Float3Value;

    }

    public TestSerializeClass _testDataInstance = null;
    public string _testSerializedData = "";

    public override void OnGUI(Paper paper, float width, float height)
    {
        using (ScrollView.Begin(paper, "sertest_scroll", width, height,
            paddingLeft: 8, paddingRight: 8, paddingTop: 8, colSpacing: 4))
        {

            EditorGUI.Button(paper, "sertest_serialize", "Serialize Test Data").OnValueChanged(pressed =>
            {
                Debug.Log($"Testing Serialization");

                _testDataInstance = new TestSerializeClass()
                {
                    IntValue = 42,
                    StringValue = "Hello, World!",
                    FloatValue = 3.14f,
                    Float3Value = new Float3(1.25f, -0.005f, 325f)
                };

                var serData = Serializer.Serialize(_testDataInstance);

                Debug.Log($"Data: {serData.WriteToString()}");
            });

            EditorGUI.TextField(paper, "sertest_data", "Serialized Data", _testSerializedData).OnValueChanged((newValue) =>
            {
                _testSerializedData = newValue;
            });

            EditorGUI.Button(paper, "sertest_deserialize", "Deserialize Test Data").OnValueChanged(pressed =>
            {
                Debug.Log($"Testing Deserialization");

                var echoObject = EchoObject.ReadFromString(_testSerializedData);
                var serData = Serializer.Deserialize<TestSerializeClass>(echoObject);

                Debug.Log($"Data: {serData.Float3Value}");
            });
        }
    }
}
