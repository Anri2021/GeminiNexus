using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeminiNexus.Server.Infrastructure;

namespace GeminiNexus.Server.Application;

public sealed class ToolExecutor(WorkspaceStore store,PluginPackageService packages)
{
    public async Task<JsonArray> Execute(string runId,JsonArray modelParts,CancellationToken ct)
    {
        var responses=new JsonArray();
        foreach(var node in modelParts)
        {
            if(node is not JsonObject part||part["functionCall"] is not JsonObject call)continue;
            var name=call["name"]?.GetValue<string>()??"";var id=call["id"]?.GetValue<string>();
            var args=call["args"] as JsonObject??new JsonObject();var status="completed";JsonNode result;
            try
            {
                result=name switch
                {
                    "nexus_utc_time"=>new JsonObject{{"utc",DateTimeOffset.UtcNow.ToString("O")}},
                    "nexus_calculate"=>Calculate(args),
                    _=>await packages.Execute(runId,name,args,ct)
                };
            }
            catch(Exception ex)when(ex is InvalidOperationException or FormatException or OverflowException)
            {status="failed";result=new JsonObject{{"error",ex.Message}};}
            var executionId=Guid.NewGuid().ToString("N");
            await store.SaveToolExecution(executionId,runId,name,args.ToJsonString(),result.ToJsonString(),status,ct);
            var response=new JsonObject{{"name",name},{"response",result}};if(id is not null)response["id"]=id;
            responses.Add((JsonNode)new JsonObject{{"functionResponse",response}});
        }
        return responses;
    }

    private static JsonObject Calculate(JsonObject args)
    {
        var expression=args["expression"]?.GetValue<string>()??throw new InvalidOperationException("חסר ביטוי");
        if(expression.Length is 0 or >1024)throw new InvalidOperationException("אורך הביטוי אינו תקין");
        var parser=new ArithmeticParser(expression.AsSpan());var value=parser.Parse();
        if(!double.IsFinite(value))throw new InvalidOperationException("התוצאה אינה סופית");
        return new JsonObject{{"value",value},{"text",value.ToString("G17",CultureInfo.InvariantCulture)}};
    }

    private ref struct ArithmeticParser
    {
        private readonly ReadOnlySpan<char> input;
        private int index;
        public ArithmeticParser(ReadOnlySpan<char> input){this.input=input;index=0;}
        public double Parse(){var value=Expression();Skip();if(index!=input.Length)throw new FormatException("הביטוי מכיל תווים לא נתמכים");return value;}
        private double Expression(){var value=Term();while(true){Skip();if(Take('+'))value+=Term();else if(Take('-'))value-=Term();else return value;}}
        private double Term(){var value=Factor();while(true){Skip();if(Take('*'))value*=Factor();else if(Take('/')){var divisor=Factor();if(divisor==0)throw new InvalidOperationException("חלוקה באפס");value/=divisor;}else return value;}}
        private double Factor(){Skip();if(Take('+'))return Factor();if(Take('-'))return-Factor();if(Take('(')){var value=Expression();Skip();if(!Take(')'))throw new FormatException("חסר סוגר");return value;}return Number();}
        private double Number(){Skip();var start=index;while(index<input.Length&&(char.IsAsciiDigit(input[index])||input[index] is '.' or 'e' or 'E' or '+' or '-')){if(index>start&&input[index] is '+' or '-'&&input[index-1] is not ('e' or 'E'))break;index++;}if(start==index||!double.TryParse(input[start..index],NumberStyles.Float,CultureInfo.InvariantCulture,out var value))throw new FormatException("מספר אינו תקין");return value;}
        private bool Take(char value){if(index<input.Length&&input[index]==value){index++;return true;}return false;}
        private void Skip(){while(index<input.Length&&char.IsWhiteSpace(input[index]))index++;}
    }
}
