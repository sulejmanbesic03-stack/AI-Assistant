using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace AI_Assistant.AI
{
    public class ChatMessage
    {
        public string Role { get; set; }
        public string Message { get; set; }
      
        public ChatMessage(string role, string message) {

            Role= role;
            Message= message;


        }

       
        
    }
}
